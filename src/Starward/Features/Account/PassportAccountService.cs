using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Starward.Core;
using Starward.Core.DeviceFp;
using Starward.Core.GameRecord;
using Starward.Core.Localization;
using Starward.Core.Passport;
using Starward.Features.Database;
using Starward.Features.GameAccount;
using Starward.Features.GameRecord;
using Starward.Features.Passport;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
// 同名的命名空间 Starward.Features.GameAccount 会遮住 GameAccount 这个类型，这里给它起个别名。
using GameAccountItem = Starward.Features.GameAccount.GameAccount;

namespace Starward.Features.Account;

/// <summary>
/// 通行证（国服）账号服务，管理主分支（main）的账号池。
/// <para>
/// 设计原则：
/// 1. 账号本身只存在数据库的 <c>PassportAccount</c> 表里，令牌用 DPAPI 加密；需要用到哪一个就把它写到「当前账号」。
/// 2. 所有米哈游接口请求都尽量只发必要的次数，设备指纹、账号信息等都会缓存复用。
/// 3. 令牌的失效特性在 <see cref="PassportTokenSet"/> 中静态区分，SToken 定期校验续期，
///    LToken / CookieToken 作为可再生令牌按需重新获取。
/// 4. 与上游 0.19 的 <see cref="PassportService"/> 共存：账号池是国服账号的主要存储，
///    同时把账号同步进上游的 <c>PassportUserInfo</c> 表，MainView / 自动登录 / 战绩等已有代码继续可用。
/// </para>
/// </summary>
internal class PassportAccountService
{

    private readonly ILogger<PassportAccountService> _logger;

    private readonly GameRecordRoleService _gameRecordRoleService;

    private readonly PassportClient _client;


    private readonly object _lock = new();


    private PassportTokenSet? _tokenSet;

    private bool _loaded;

    private bool _initialized;

    /// <summary>同一时间只允许一个刷新流程，避免重复请求。</summary>
    private Task<bool>? _refreshing;


    private AndroidDeviceInfo? _deviceInfo;

    private readonly SemaphoreSlim _deviceInfoLock = new(1, 1);


    public PassportAccountService(ILogger<PassportAccountService> logger, GameRecordRoleService gameRecordRoleService, PassportClient client)
    {
        _logger = logger;
        _gameRecordRoleService = gameRecordRoleService;
        _client = client;
    }


    /// <summary>
    /// 所有可能记录了「当前使用的通行证账号」的游戏区服。删除账号时要把这些记录一起清掉。
    /// </summary>
    private static readonly GameBiz[] AllGameBizs =
    [
        GameBiz.bh3_cn, GameBiz.bh3_global,
        GameBiz.hk4e_cn, GameBiz.hk4e_global, GameBiz.hk4e_bilibili,
        GameBiz.hkrpg_cn, GameBiz.hkrpg_global, GameBiz.hkrpg_bilibili,
        GameBiz.nap_cn, GameBiz.nap_global, GameBiz.nap_bilibili,
    ];




    #region 本地状态




    public PassportTokenSet? TokenSet
    {
        get
        {
            EnsureLoaded();
            return _tokenSet;
        }
    }


    public bool IsLoggedIn => TokenSet?.HasStoken ?? false;


    public long Aid => TokenSet?.Aid ?? 0;


    /// <summary>
    /// 当前会话对应的账号在账号池里的标识（见 <see cref="PassportAccountRecord.GetKey"/>），未登录时为 null。
    /// 用来判断会话是不是已经等于某个预设 / 游戏记住的账号，避免重复切换。
    /// </summary>
    public string? CurrentAccountKey
    {
        get
        {
            var tokenSet = TokenSet;
            return tokenSet?.HasStoken is true ? PassportAccountRecord.GetKey(tokenSet) : null;
        }
    }


    public string? AccountName => TokenSet?.AccountName;


    /// <summary>
    /// 界面上显示的账号名称：米游社昵称 → 手机号 → 通行证 ID。
    /// 昵称保存在本机数据库中，只有缺失时才会查一次数据库，不会产生网络请求。
    /// </summary>
    public string? DisplayName
    {
        get
        {
            var tokenSet = TokenSet;
            if (tokenSet is null)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(tokenSet.Nickname) && tokenSet.Aid > 0)
            {
                string? nickname = GameRecordRoleService.GetRecordUser(tokenSet.Aid, false)?.Nickname;
                if (!string.IsNullOrWhiteSpace(nickname))
                {
                    tokenSet.Nickname = nickname;
                    SaveTokenSet(tokenSet);
                }
            }
            return tokenSet.DisplayName;
        }
    }


    public DateTimeOffset LoginTime => TokenSet?.LoginTime ?? default;


    /// <summary>当前账号在米游社工具箱里的头像，读不到时返回 null，界面会退回到通用图标。</summary>
    public string? CurrentAvatarUrl => GetAccountAvatarUrl(Aid);




    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }
            try
            {
                string? stored = AppConfig.PassportTokenJson;
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    // 令牌用 DPAPI 加密保存；解不开说明是旧版本的明文，直接按 JSON 解析并重新加密保存。
                    string? json = DpapiHelper.Unprotect(stored);
                    bool isPlainText = string.IsNullOrWhiteSpace(json);
                    if (isPlainText)
                    {
                        json = stored;
                    }
                    _tokenSet = JsonSerializer.Deserialize<PassportTokenSet>(json!, PassportJson.Options);
                    if (isPlainText && _tokenSet is not null)
                    {
                        _logger.LogInformation("Migrating plain text passport tokens to DPAPI protected storage.");
                        SaveTokenSet(_tokenSet);
                    }
                }
                MigrateLegacyConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load passport token set");
            }
            finally
            {
                _loaded = true;
            }
        }
    }


    private void SaveTokenSet(PassportTokenSet? tokenSet)
    {
        _tokenSet = tokenSet;
        // 令牌属于敏感信息，落库前用 DPAPI 加密，只有当前 Windows 用户能够解密。
        string? json = tokenSet is null ? null : JsonSerializer.Serialize(tokenSet, PassportJson.Options);
        AppConfig.PassportTokenJson = DpapiHelper.Protect(json);
        // 当前账号同时也是账号列表里的一条记录，切换账号时直接从列表取回。
        if (tokenSet is not null)
        {
            TrySaveAccountRecord(tokenSet);
            // 刚写入的令牌是有效的：把这个账号从“登录已失效”里移掉，界面上的红点跟着消失。
            lock (_lock)
            {
                _expiredAccountKeys.Remove(PassportAccountRecord.GetKey(tokenSet));
            }
            // 让上游 MainView / 自动登录读到的 PassportUserInfo 与账号池保持一致。
            SyncAccountToPassportUserInfo(tokenSet);
            if (tokenSet.Aid > 0)
            {
                AppConfig.SetLastPassportUserAid(tokenSet.Region, tokenSet.Aid);
            }
        }
        WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(IsLoggedIn));
    }


    /// <summary>
    /// 保存刷新后的令牌：正好是当前账号时连同会话一起更新，否则只写回账号池，
    /// 避免刷新账号池时把别的账号顶成当前账号。
    /// </summary>
    private void SaveTokenSetOrRecord(PassportTokenSet tokenSet)
    {
        var current = TokenSet;
        if (current?.HasStoken is true && PassportAccountRecord.GetKey(current) == PassportAccountRecord.GetKey(tokenSet))
        {
            SaveTokenSet(tokenSet);
        }
        else
        {
            TrySaveAccountRecord(tokenSet);
            SyncAccountToPassportUserInfo(tokenSet);
        }
    }


    /// <summary>
    /// 退出当前会话。账号记录会留在账号列表里（就是它保存的令牌），
    /// 之后可以直接切回来，不需要重新输入密码；要真正删掉账号、并让服务端吊销会话请用 <see cref="RemoveAccount"/>。
    /// <para>
    /// 只清掉「当前账号」，不动账号池与上游 <c>PassportUserInfo</c> 表：那两处表示「本机保存了哪些账号」，
    /// 与当前选中哪个账号无关。同理也不向服务端登出——登出会让账号池里保存的令牌失效，
    /// 切回来时又得重新登录。
    /// </para>
    /// </summary>
    public void Logout()
    {
        try
        {
            var tokenSet = _tokenSet;
            SaveTokenSet(null);
            if (tokenSet is not null)
            {
                if (AppConfig.GetLastPassportUserAid(tokenSet.Region) == tokenSet.Aid)
                {
                    AppConfig.SetLastPassportUserAid(tokenSet.Region, 0);
                }
            }
            _logger.LogInformation("Passport account signed out, the account record is kept.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout passport account");
        }
    }


    /// <summary>
    /// 通知通行证服务端吊销这个账号的会话。失败不影响本地退出（令牌已经清掉了）。
    /// </summary>
    private async Task LogoutFromServerAsync(PassportTokenSet tokenSet)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tokenSet.Stoken) || string.IsNullOrWhiteSpace(tokenSet.Mid))
            {
                return;
            }
            await _client.LogoutAsync(new PassportUserInfo
            {
                Region = tokenSet.Region,
                Aid = tokenSet.Aid,
                Mid = tokenSet.Mid,
                Stoken = tokenSet.Stoken,
            }, CancellationToken.None);
            _logger.LogInformation("Passport session revoked on the server (aid: {aid}).", tokenSet.Aid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Revoke the passport session on the server (aid: {aid})", tokenSet.Aid);
        }
    }




    #endregion




    #region 设备指纹




    /// <summary>
    /// 准备设备指纹。游戏 / 通行证请求都要用到同一枚指纹，所以这里沿用 0.19 的
    /// <see cref="DeviceFpClient"/>（通过 <see cref="GameRecordServiceBase.GetDeviceInfoAsync"/> 缓存与申请），
    /// 并且保留主分支的「账号已经登记过的真实指纹」优先逻辑。
    /// </summary>
    public async Task PrepareDeviceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AndroidDeviceInfo info = await EnsureDeviceInfoAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(info.BbsDeviceId))
            {
                _client.DeviceId = info.BbsDeviceId;
            }
            if (!string.IsNullOrWhiteSpace(info.DeviceFp))
            {
                _client.DeviceFp = info.DeviceFp;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare device fingerprint");
        }
    }


    private async Task<AndroidDeviceInfo> EnsureDeviceInfoAsync(CancellationToken cancellationToken)
    {
        if (_deviceInfo is not null)
        {
            return _deviceInfo;
        }
        await _deviceInfoLock.WaitAsync(cancellationToken);
        try
        {
            if (_deviceInfo is not null)
            {
                return _deviceInfo;
            }
            AndroidDeviceInfo info = await _gameRecordRoleService.GetDeviceInfoAsync(ServerRegion.miHoYo, cancellationToken);
            // 旧 main 登记过的指纹优先：即使 0.19 已经先存过一份随机设备信息（工具箱请求可能早于这里初始化），
            // 也要换回账号登记过的那枚，否则服务端的「账号-设备」绑定会被打破。
            // 换完 AndroidDeviceInfo 里就是唯一一份，旧键同时删掉。
            AdoptRegisteredDeviceFp(info);
            _deviceInfo = info;
            return info;
        }
        finally
        {
            _deviceInfoLock.Release();
        }
    }


    /// <summary>
    /// 升级自 main 时沿用账号已经登记过的真实设备指纹。
    /// <para>
    /// 主分支把设备号 / 指纹保存在设置项 <c>HyperionDeviceId</c> / <c>HyperionDeviceFp</c> 里（v22 迁移会原样保留），
    /// 再往后是工具箱 Cookie 里的 <c>DEVICEFP</c>。换指纹会破坏服务端「账号 - 设备」的绑定关系，
    /// 所以首次升级、0.19 还没有自己的设备信息时，优先采用旧库里已经登记过的那一枚，而不是新申请一枚。
    /// </para>
    /// </summary>
    private void AdoptRegisteredDeviceFp(AndroidDeviceInfo info)
    {
        try
        {
            string? deviceFp = ReadLegacySetting("HyperionDeviceFp");
            string? deviceId = ReadLegacySetting("HyperionDeviceId");
            if (string.IsNullOrWhiteSpace(deviceFp))
            {
                (deviceFp, deviceId) = FindRegisteredDeviceFpInToolbox();
            }
            if (string.IsNullOrWhiteSpace(deviceFp) || deviceFp is "0000000000000")
            {
                return;
            }
            info.DeviceFp = deviceFp;
            info.BbsDeviceFp = deviceFp;
            if (Guid.TryParse(deviceId, out _))
            {
                info.BbsDeviceId = deviceId!;
            }
            AppConfig.AndroidDeviceInfo = JsonSerializer.Serialize(info, DeviceFpJsonContext.Default.AndroidDeviceInfo);
            // 设备信息现在只有 AndroidDeviceInfo 一个 owner：旧 main 的 Hyperion* 键只是迁移输入，
            // 留着会和 AndroidDeviceInfo 变成两份互相不更新的副本。
            DatabaseService.DeleteSettings("HyperionDeviceId", "HyperionDeviceFp", "HyperionDeviceFpLastUpdateTime", "HyperionDeviceAndroidId");
            _logger.LogInformation("Adopted the device fingerprint registered by the account ({deviceFp}).", deviceFp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adopt the registered device fingerprint");
        }
    }


    /// <summary>
    /// 在工具箱账号 / 角色保存的 Cookie 里找账号已经登记过的设备指纹（<c>DEVICEFP</c> / <c>x-rpc-device_fp</c>）。
    /// 只读数据库，不产生任何请求。
    /// </summary>
    private (string? DeviceFp, string? DeviceId) FindRegisteredDeviceFpInToolbox()
    {
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            var cookies = dapper.Query<string>("SELECT Cookie FROM GameRecordRole WHERE Cookie IS NOT NULL AND Cookie <> '';").ToList();
            cookies.AddRange(dapper.Query<string>("SELECT Cookie FROM GameRecordUser WHERE Cookie IS NOT NULL AND Cookie <> '';"));
            foreach (string cookie in cookies)
            {
                PassportCookieParseResult parsed = PassportCookieParser.Parse(cookie);
                if (!string.IsNullOrWhiteSpace(parsed.DeviceFp))
                {
                    return (parsed.DeviceFp, null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read the registered device fingerprint from the toolbox cookies");
        }
        return (null, null);
    }


    /// <summary>
    /// 直接读取旧 main 分支留在 Setting 表里的原始设置值（这些键在 0.19 里没有对应的 AppConfig 属性）。
    /// </summary>
    private static string? ReadLegacySetting(string key)
    {
        try
        {
            return AppConfig.GetValue<string>(null, key);
        }
        catch
        {
            return null;
        }
    }




    #endregion




    #region 登录：账号密码




    /// <summary>
    /// 账号密码登录。若返回 <see cref="PassportLoginState.NeedCaptcha"/>，
    /// 需要完成人机验证后带上 <paramref name="captcha"/> 重试同一个方法。
    /// </summary>
    public async Task<PassportLoginOutcome> LoginByPasswordAsync(string account, string password, PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            var result = await _client.LoginByPasswordAsync(account, password, captcha, cancellationToken);
            return await HandleLoginResultAsync(result, "password", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login by password");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    #endregion




    #region 登录：短信验证码




    /// <summary>
    /// 发送登录短信验证码。返回的 <c>action_type</c> 需要在登录时原样带回。
    /// </summary>
    public async Task<PassportLoginOutcome> SendSmsCaptchaAsync(string mobile, PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            var result = await _client.CreateLoginCaptchaAsync(mobile, "+86", captcha, cancellationToken);
            if (result.IsCaptchaRequired)
            {
                return PassportLoginOutcome.Captcha(result);
            }
            if (!result.IsSuccess)
            {
                return PassportLoginOutcome.Failed(result);
            }
            var data = result.Deserialize<PassportMobileCaptchaData>();
            var outcome = PassportLoginOutcome.Succeeded();
            outcome.ActionType = data?.ActionType;
            outcome.Countdown = data?.Countdown ?? 60;
            return outcome;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send login sms captcha");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    /// <summary>
    /// 短信验证码登录。
    /// </summary>
    public async Task<PassportLoginOutcome> LoginBySmsCaptchaAsync(string mobile, string code, string? actionType, PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            var result = await _client.LoginByMobileCaptchaAsync(mobile, code, actionType, "+86", captcha, cancellationToken);
            return await HandleLoginResultAsync(result, "sms", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login by sms captcha");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    #endregion




    #region 登录：新设备短信校验




    /// <summary>
    /// 新设备登录校验：发送验证码。
    /// </summary>
    public async Task<PassportLoginOutcome> SendDeviceVerifyCaptchaAsync(string actionTicket, PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            var result = await _client.CreateMobileCaptchaByActionTicketAsync(actionTicket, captcha, cancellationToken);
            if (result.IsCaptchaRequired)
            {
                return PassportLoginOutcome.Captcha(result);
            }
            if (!result.IsSuccess)
            {
                return PassportLoginOutcome.Failed(result);
            }
            return PassportLoginOutcome.Succeeded();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send new device sms captcha");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    /// <summary>
    /// 新设备登录校验：提交验证码并换取登录信息。
    /// </summary>
    public async Task<PassportLoginOutcome> CompleteDeviceVerifyAsync(string actionTicket, string verifyTicket, string code, CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            var result = await _client.VerifyActionTicketPartlyAsync(verifyTicket, code, 1, cancellationToken);
            if (!result.IsSuccess)
            {
                return PassportLoginOutcome.Failed(result);
            }
            // 校验通过后不再需要重新提交账号密码。
            result = await _client.CheckRiskVerifiedAsync(actionTicket, cancellationToken);
            return await HandleLoginResultAsync(result, "device-verify", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Complete new device verification");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    #endregion




    #region 登录：扫码




    /// <summary>
    /// 申请扫码登录二维码。
    /// </summary>
    public async Task<PassportQrSession?> StartQrLoginAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            var result = await _client.CreateQrLoginAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Create qr login failed: {retcode} {message}", result.Retcode, result.Message);
                return null;
            }
            var ticket = result.Deserialize<PassportQrTicket>();
            if (string.IsNullOrWhiteSpace(ticket?.Url) || string.IsNullOrWhiteSpace(ticket.Ticket))
            {
                return null;
            }
            return new PassportQrSession { Url = ticket.Url, Ticket = ticket.Ticket, ExpiresIn = ticket.ExpiresIn };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start qr login");
            return null;
        }
    }




    /// <summary>
    /// 查询扫码状态。已确认时直接完成登录。
    /// </summary>
    public async Task<PassportLoginOutcome> PollQrLoginAsync(PassportQrSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.QueryQrLoginStatusAsync(session.Ticket, cancellationToken);
            if (result.Retcode is -3501)
            {
                session.IsExpired = true;
                return PassportLoginOutcome.Failed(result);
            }
            if (result.Retcode is -3505)
            {
                session.IsCancelled = true;
                return PassportLoginOutcome.Failed(result);
            }
            if (!result.IsSuccess)
            {
                if (result.IsCaptchaRequired)
                {
                    return PassportLoginOutcome.Captcha(result);
                }
                return PassportLoginOutcome.Failed(result);
            }
            var status = result.Deserialize<PassportQrStatusData>();
            if (status is null)
            {
                return PassportLoginOutcome.Failed(result);
            }
            session.IsScanned = status.IsScanned;
            if (!status.IsConfirmed)
            {
                return PassportLoginOutcome.Pending();
            }
            // 米游社 App 版接口会直接在 tokens 中返回 SToken。
            string? stoken = status.Tokens?.FirstOrDefault()?.Token;
            if (!string.IsNullOrWhiteSpace(stoken))
            {
                var tokenSet = CreateTokenSet(stoken, status.UserInfo?.Mid, status.UserInfo?.Aid ?? 0, "qr");
                if (status.UserInfo is null)
                {
                    await EnrichUserInfoAsync(tokenSet, cancellationToken);
                }
                return await FinishLoginAsync(tokenSet, cancellationToken);
            }
            // 兜底：部分接口需要再用票据换取登录信息。
            var exchange = await _client.LoginByAuthTicketAsync(session.Ticket, cancellationToken);
            return await HandleLoginResultAsync(exchange, "qr", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll qr login status");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    #endregion




    #region 登录：手动导入令牌




    /// <summary>
    /// 手动导入 SToken。适用于用户已经在其它地方获取过令牌的情况。
    /// <para>
    /// V2 的 SToken 必须配 mid；V1 的 SToken 必须配 stuid（<paramref name="aid"/>），
    /// 后者会先转换成 V2 再使用。
    /// </para>
    /// </summary>
    public async Task<PassportLoginOutcome> ImportStokenAsync(string stoken, string? mid, long aid = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            stoken = stoken.Trim();
            mid = mid?.Trim();
            await PrepareDeviceAsync(cancellationToken);
            var tokenSet = CreateTokenSet(stoken, mid, aid, "import");
            if (tokenSet.StokenVersion == 2)
            {
                if (string.IsNullOrWhiteSpace(tokenSet.Mid))
                {
                    return PassportLoginOutcome.Failed(new miHoYoApiException(-1, Lang.PassportLogin_ImportNeedStokenV2));
                }
                var result = await _client.VerifyStokenAsync(tokenSet.Stoken!, tokenSet.Mid!, false, cancellationToken);
                if (!result.IsSuccess)
                {
                    return PassportLoginOutcome.Failed(result);
                }
                var data = result.Deserialize<PassportVerifyData>();
                ApplyUserInfo(tokenSet, data?.UserInfo);
            }
            else if (tokenSet.Aid <= 0 && string.IsNullOrWhiteSpace(tokenSet.Mid))
            {
                return PassportLoginOutcome.Failed(new miHoYoApiException(-1, Lang.PassportLogin_ImportNeedStuid));
            }
            // V1 的转换与派生令牌的获取都在 FinishLoginAsync 里完成。
            return await FinishLoginAsync(tokenSet, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import stoken");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    #endregion




    #region 登录结果处理




    private PassportTokenSet CreateTokenSet(string stoken, string? mid, long aid, string method)
    {
        return new PassportTokenSet
        {
            Region = ServerRegion.miHoYo,
            Stoken = stoken,
            StokenVersion = stoken.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1,
            Mid = mid,
            Aid = aid,
            LoginTime = DateTimeOffset.Now,
            LoginMethod = method,
        };
    }




    private static void ApplyUserInfo(PassportTokenSet tokenSet, PassportUserInfo? userInfo)
    {
        if (userInfo is null)
        {
            return;
        }
        if (userInfo.Aid > 0)
        {
            tokenSet.Aid = userInfo.Aid;
        }
        if (!string.IsNullOrWhiteSpace(userInfo.Mid))
        {
            tokenSet.Mid = userInfo.Mid;
        }
        tokenSet.AccountName = GetDisplayAccountName(userInfo);
    }




    private static string? GetDisplayAccountName(PassportUserInfo userInfo)
    {
        // 界面只展示手机号，拿不到时由通行证 ID 兜底。
        return string.IsNullOrWhiteSpace(userInfo.Mobile) ? null : userInfo.Mobile;
    }




    /// <summary>
    /// 处理登录族接口（密码 / 短信 / 新设备校验）的返回。
    /// </summary>
    private async Task<PassportLoginOutcome> HandleLoginResultAsync(PassportResult result, string method, CancellationToken cancellationToken)
    {
        if (result.IsCaptchaRequired)
        {
            return PassportLoginOutcome.Captcha(result);
        }
        if (result.IsNewDevice)
        {
            return PassportLoginOutcome.DeviceVerify(result);
        }
        if (!result.IsSuccess)
        {
            return PassportLoginOutcome.Failed(result);
        }
        var data = result.Deserialize<PassportLoginData>();
        string? stoken = data?.Token?.Token;
        if (string.IsNullOrWhiteSpace(stoken))
        {
            // 部分返回只给出 login_ticket，需要再换一次令牌。
            return PassportLoginOutcome.Failed(new miHoYoApiException(-1, Lang.PassportLogin_NoTokenReturned));
        }
        var tokenSet = CreateTokenSet(stoken, data!.UserInfo?.Mid, data.UserInfo?.Aid ?? 0, method);
        ApplyUserInfo(tokenSet, data.UserInfo);
        if (data.UserInfo is null)
        {
            await EnrichUserInfoAsync(tokenSet, cancellationToken);
        }
        return await FinishLoginAsync(tokenSet, cancellationToken);
    }




    /// <summary>
    /// 登录信息里缺少账号资料时，通过校验接口补齐（同时也会刷新 SToken）。
    /// </summary>
    private async Task EnrichUserInfoAsync(PassportTokenSet tokenSet, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenSet.Mid))
        {
            return;
        }
        var result = await _client.VerifyStokenAsync(tokenSet.Stoken!, tokenSet.Mid!, true, cancellationToken);
        if (result.IsSuccess)
        {
            var data = result.Deserialize<PassportVerifyData>();
            ApplyUserInfo(tokenSet, data?.UserInfo);
            if (!string.IsNullOrWhiteSpace(data?.NewToken?.Token))
            {
                tokenSet.Stoken = data.NewToken.Token;
                tokenSet.StokenVersion = tokenSet.Stoken!.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1;
            }
            tokenSet.StokenVerifyTime = DateTimeOffset.Now;
        }
    }




    /// <summary>
    /// 登录收尾：确保拿到 SToken（V2）、拉取米游社所需的派生令牌，并与工具箱同步。
    /// <para>
    /// SToken 一旦拿到就会入库——它是长期凭据，不能因为派生令牌换不到就丢掉、让用户重新登录。
    /// 但派生令牌换不到意味着工具箱 / 签到拿不到数据，所以这种情况按 <b>失败</b> 返回，
    /// 让界面显示原因而不是假的“登录成功”。
    /// </para>
    /// </summary>
    private async Task<PassportLoginOutcome> FinishLoginAsync(PassportTokenSet tokenSet, CancellationToken cancellationToken)
    {
        // SToken（V1）必须与 stuid 搭配，转换为 V2 后才能与 mid 搭配用于通行证会话。
        if (tokenSet.StokenVersion == 1 && tokenSet.Aid > 0)
        {
            var convert = await _client.GetTokenBySTokenAsync(tokenSet.Stoken!, tokenSet.Aid, cancellationToken);
            if (convert.IsSuccess)
            {
                var info = convert.Deserialize<PassportTokenInfo>();
                if (!string.IsNullOrWhiteSpace(info?.Token))
                {
                    tokenSet.Stoken = info.Token;
                    tokenSet.StokenVersion = info.Token!.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1;
                }
                if (!string.IsNullOrWhiteSpace(info?.Mid))
                {
                    tokenSet.Mid = info.Mid;
                }
            }
        }
        // 登录本身已经由服务端校验过令牌，记录下来可以避免登录后立刻再发一次校验请求。
        tokenSet.StokenVerifyTime = DateTimeOffset.Now;
        SaveTokenSet(tokenSet);
        var (derived, derivedMessage) = await RefreshDerivedTokensAsync(tokenSet, cancellationToken);
        await SyncToToolboxAsync(tokenSet, cancellationToken);
        if (!derived)
        {
            _logger.LogWarning("Passport account signed in, but the toolbox credentials could not be exchanged (aid: {aid}, method: {method}): {reason}",
                               tokenSet.Aid, tokenSet.LoginMethod, derivedMessage);
            var failed = PassportLoginOutcome.Failed(string.Format(Lang.PassportLogin_DerivedTokenFailed, derivedMessage ?? Lang.PassportLogin_ServiceError));
            failed.Aid = tokenSet.Aid;
            failed.Mid = tokenSet.Mid;
            failed.AccountName = tokenSet.AccountName;
            return failed;
        }
        _logger.LogInformation("Passport account logged in ({method}, aid: {aid}).", tokenSet.LoginMethod, tokenSet.Aid);
        var outcome = PassportLoginOutcome.Succeeded();
        outcome.Aid = tokenSet.Aid;
        outcome.Mid = tokenSet.Mid;
        outcome.AccountName = tokenSet.AccountName;
        outcome.DisplayName = tokenSet.DisplayName;
        return outcome;
    }




    #endregion




    #region 派生令牌与自动刷新




    /// <summary>
    /// 获取（刷新）LToken 与 CookieToken。这两个令牌会随时间失效，但只要 SToken 有效就可以重新换取。
    /// <para>
    /// 换票走通行证 V2 会话接口（<c>account/ma-cn-session/app/exchange</c>），旧接口正在按账号停用。
    /// 返回（是否拿到完整的派生令牌，失败原因）。失败原因已本地化，供登录收尾时提示用户。
    /// </para>
    /// </summary>
    private async Task<(bool Success, string? Message)> RefreshDerivedTokensAsync(PassportTokenSet tokenSet, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenSet.Stoken))
        {
            return (false, CoreLang.PassportError_TokenExpired);
        }
        if (string.IsNullOrWhiteSpace(tokenSet.Mid))
        {
            // V2 换票接口必须同时提供 stoken 与 mid。
            _logger.LogWarning("Can not exchange derived tokens without mid (aid: {aid}).", tokenSet.Aid);
            return (false, CoreLang.PassportError_MidRequired);
        }
        try
        {
            string? failureMessage = null;
            var ltokenResult = await _client.GetLTokenBySTokenAsync(tokenSet.Stoken!, tokenSet.Mid!, cancellationToken);
            if (ltokenResult.IsSuccess)
            {
                var data = ltokenResult.Deserialize<PassportExchangeData>();
                string? ltoken = data?.Token?.Token;
                // 服务端报回来的 UID 不可信：遇到过它和账号本身不是一个人。
                // 不匹配就丢掉这个令牌，别把它写进 Cookie 造成串号。
                long owner = data?.UserInfo?.Aid ?? 0;
                if (owner > 0 && owner != tokenSet.Aid)
                {
                    _logger.LogWarning("LToken owner {owner} does not match account {aid}, drop it.", owner, tokenSet.Aid);
                    tokenSet.Ltoken = null;
                    tokenSet.LtokenUid = 0;
                    failureMessage ??= Lang.PassportLogin_DerivedTokenOwnerMismatch;
                }
                else if (!string.IsNullOrWhiteSpace(ltoken))
                {
                    tokenSet.Ltoken = ltoken;
                    // Cookie 里用的是账号自己的 ID，这里只记下服务端报的值备查。
                    tokenSet.LtokenUid = owner > 0 ? owner : tokenSet.Aid;
                }
                else
                {
                    failureMessage ??= Lang.PassportLogin_NoTokenReturned;
                }
            }
            else
            {
                failureMessage ??= LogDerivedTokenFailure("ltoken", ltokenResult);
            }
            var cookieResult = await _client.GetCookieTokenBySTokenAsync(tokenSet.Stoken!, tokenSet.Mid!, cancellationToken);
            if (cookieResult.IsSuccess)
            {
                var data = cookieResult.Deserialize<PassportExchangeData>();
                long owner = data?.UserInfo?.Aid ?? 0;
                if (owner > 0 && owner != tokenSet.Aid)
                {
                    _logger.LogWarning("Cookie token owner {owner} does not match account {aid}, ignore it.", owner, tokenSet.Aid);
                    failureMessage ??= Lang.PassportLogin_DerivedTokenOwnerMismatch;
                }
                else if (!string.IsNullOrWhiteSpace(data?.Token?.Token))
                {
                    tokenSet.CookieToken = data.Token.Token;
                }
                else
                {
                    failureMessage ??= Lang.PassportLogin_NoTokenReturned;
                }
            }
            else
            {
                failureMessage ??= LogDerivedTokenFailure("cookie token", cookieResult);
            }
            // 只换到 ltoken、没换到 cookie_token 不算刷新完成：工具箱的部分接口需要 cookie_token。
            bool success = tokenSet.HasDerivedToken && !string.IsNullOrWhiteSpace(tokenSet.CookieToken);
            if (success)
            {
                tokenSet.DerivedTokenTime = DateTimeOffset.Now;
            }
            if (tokenSet.HasDerivedToken)
            {
                // 换到什么就先存什么：下次刷新时只缺的那部分会再试一次。
                SaveTokenSetOrRecord(tokenSet);
            }
            return (success, success ? null : failureMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh derived tokens");
            return (false, ex.Message);
        }
    }




    /// <summary>
    /// 记录换票失败的日志，并区分「服务端拒绝」与「凭据失效」：
    /// 1002 / -464 是接口被停用或风控，真正需要重新登录的是 -100 / -111 这类。
    /// </summary>
    private string LogDerivedTokenFailure(string name, PassportResult result)
    {
        if (result.Retcode is 1002 or -464)
        {
            _logger.LogWarning("Exchange {name} rejected by the passport service: {retcode} {message}", name, result.Retcode, result.Message);
        }
        else if (result.Retcode is -100 or -111 or 10001 or -10001)
        {
            _logger.LogWarning("Exchange {name} failed because the credentials are no longer valid: {retcode} {message}", name, result.Retcode, result.Message);
        }
        else
        {
            _logger.LogWarning("Exchange {name} failed: {retcode} {message}", name, result.Retcode, result.Message);
        }
        return PassportError.GetMessage(result.Retcode, result.Message);
    }




    /// <summary>
    /// 按需刷新令牌。会在以下情况触发实际请求：
    /// SToken 超过校验间隔、派生令牌缺失或超过有效期、或者 <paramref name="force"/> 为 true（例如接口返回鉴权失败）。
    /// </summary>
    public async Task<bool> RefreshIfNeededAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var tokenSet = TokenSet;
        if (tokenSet is null || !tokenSet.HasStoken)
        {
            return false;
        }
        var now = DateTimeOffset.Now;
        bool needVerify = force || tokenSet.NeedVerifyStoken(now);
        bool needDerived = force || tokenSet.NeedRefreshDerivedToken(now);
        if (!needVerify && !needDerived)
        {
            return true;
        }
        lock (_lock)
        {
            _refreshing ??= RefreshCoreAsync(tokenSet, needVerify, needDerived, cancellationToken);
        }
        try
        {
            return await _refreshing;
        }
        finally
        {
            lock (_lock)
            {
                _refreshing = null;
            }
        }
    }




    private async Task<bool> RefreshCoreAsync(PassportTokenSet tokenSet, bool needVerify, bool needDerived, CancellationToken cancellationToken)
    {
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            return await RefreshAccountAsync(tokenSet, needVerify, needDerived, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh passport tokens");
            return false;
        }
    }




    /// <summary>
    /// 刷新指定账号的令牌：SToken 校验会顺带续期，派生令牌按需重新换取。
    /// 令牌写回它自己的位置（当前会话或账号池），不会把别的账号变成当前账号。
    /// </summary>
    private async Task<bool> RefreshAccountAsync(PassportTokenSet tokenSet, bool needVerify, bool needDerived, CancellationToken cancellationToken)
    {
        if (needVerify && !string.IsNullOrWhiteSpace(tokenSet.Mid))
        {
            var result = await _client.VerifyStokenAsync(tokenSet.Stoken!, tokenSet.Mid!, true, cancellationToken);
            if (!result.IsSuccess)
            {
                // -100 / -111 表示会话已经失效，需要重新登录。
                if (result.Retcode is -100 or -111 or 10001 or -10001)
                {
                    _logger.LogWarning("Stoken is no longer valid (aid: {aid}): {retcode} {message}", tokenSet.Aid, result.Retcode, result.Message);
                    return false;
                }
            }
            else
            {
                var data = result.Deserialize<PassportVerifyData>();
                if (!string.IsNullOrWhiteSpace(data?.NewToken?.Token))
                {
                    tokenSet.Stoken = data.NewToken.Token;
                    tokenSet.StokenVersion = tokenSet.Stoken!.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1;
                }
                ApplyUserInfo(tokenSet, data?.UserInfo);
                tokenSet.StokenVerifyTime = DateTimeOffset.Now;
                SaveTokenSetOrRecord(tokenSet);
            }
        }
        if (needDerived)
        {
            var (derived, _) = await RefreshDerivedTokensAsync(tokenSet, cancellationToken);
            if (!derived)
            {
                return false;
            }
            await SyncToToolboxAsync(tokenSet, cancellationToken);
        }
        return true;
    }




    /// <summary>
    /// 手动刷新账号池里所有账号的凭据。
    /// <para>
    /// 只对确实到期的账号发请求：SToken 超过 6 小时没有校验、派生令牌缺失或者超过 30 天。
    /// 返回（已刷新的账号数，登录已失效的账号数，登录已失效的账号标识）。
    /// 失效的账号标识供账号中心在对应账号上显示警告红点。
    /// </para>
    /// </summary>
    public async Task<(int Refreshed, int Expired, IReadOnlyList<string> ExpiredKeys)> RefreshAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        int refreshed = 0;
        var expiredKeys = new List<string>();
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            foreach (var record in QueryAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is not true)
                {
                    continue;
                }
                var now = DateTimeOffset.Now;
                bool needVerify = tokenSet.NeedVerifyStoken(now);
                bool needDerived = tokenSet.NeedRefreshDerivedToken(now);
                if (!needVerify && !needDerived)
                {
                    continue;
                }
                if (await RefreshAccountAsync(tokenSet, needVerify, needDerived, cancellationToken))
                {
                    SaveTokenSetOrRecord(tokenSet);
                    refreshed++;
                }
                else
                {
                    expiredKeys.Add(record.Key);
                }
            }
            if (expiredKeys.Count > 0)
            {
                lock (_lock)
                {
                    foreach (string key in expiredKeys)
                    {
                        _expiredAccountKeys.Add(key);
                    }
                }
            }
            _accountItems = null;
            SyncToPassportUserInfo();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh all passport accounts");
        }
        return (refreshed, expiredKeys.Count, expiredKeys);
    }




    /// <summary>
    /// 校验账号池里所有账号的登录状态（强制向服务端校验一次 SToken），返回登录已失效的账号标识。
    /// <para>
    /// 账号中心每次打开时在后台跑一次：不需要用户手动点“刷新信息”也能看到哪个账号失效了。
    /// 只校验会话本身，不换取派生令牌，请求量按账号数计。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> VerifyAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        var expiredKeys = new List<string>();
        try
        {
            await PrepareDeviceAsync(cancellationToken);
            foreach (var record in QueryAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is not true || tokenSet.Aid <= 0 || string.IsNullOrWhiteSpace(tokenSet.Mid))
                {
                    continue;
                }
                if (await RefreshAccountAsync(tokenSet, needVerify: true, needDerived: false, cancellationToken))
                {
                    SaveTokenSetOrRecord(tokenSet);
                }
                else
                {
                    expiredKeys.Add(record.Key);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            lock (_lock)
            {
                _expiredAccountKeys.Clear();
                foreach (string key in expiredKeys)
                {
                    _expiredAccountKeys.Add(key);
                }
            }
            _accountItems = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verify all passport accounts");
        }
        return expiredKeys;
    }




    /// <summary>
    /// 强制刷新账号池里每个账号的米游社资料（昵称、头像、头像框），返回刷新成功的账号数。
    /// <para>
    /// 账号中心“刷新信息”按钮用：资料写在 <c>GameRecordUser</c> 表里，账号列表与工具箱共用。
    /// 国际服账号只更新资料，不做工具箱同步。
    /// </para>
    /// </summary>
    public async Task<int> RefreshAccountUserInfosAsync(CancellationToken cancellationToken = default)
    {
        int refreshed = 0;
        try
        {
            foreach (var record in QueryOrderedAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is not true || tokenSet.Aid <= 0)
                {
                    continue;
                }
                string? cookie = tokenSet.CreateToolboxCookie();
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    continue;
                }
                var user = await AddOrUpdateRecordUserAsync(tokenSet, cookie, cancellationToken);
                if (user is null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(user.Nickname) && user.Nickname != tokenSet.Nickname)
                {
                    tokenSet.Nickname = user.Nickname;
                    SaveTokenSetOrRecord(tokenSet);
                }
                refreshed++;
            }
            _accountItems = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh the profile of the passport accounts");
        }
        return refreshed;
    }




    /// <summary>
    /// 保存用户在导入令牌时提供的设备标识（<c>x-rpc-device_id</c>）。
    /// <para>
    /// 服务端只认账号已经登记过的那一枚设备号，所以这里只接受用户从浏览器请求头抄来的 GUID，
    /// 写入设备信息后立即对后续通行证请求生效，不做任何自动获取 / 登记。
    /// </para>
    /// </summary>
    /// <returns>写入成功返回 true；格式不合法返回 false。</returns>
    public bool SaveDeviceId(string? deviceId)
    {
        deviceId = deviceId?.Trim();
        if (string.IsNullOrWhiteSpace(deviceId) || !Guid.TryParse(deviceId, out Guid parsed))
        {
            return false;
        }
        deviceId = parsed.ToString("D");
        try
        {
            AndroidDeviceInfo? info = _deviceInfo;
            if (info is null && !string.IsNullOrWhiteSpace(AppConfig.AndroidDeviceInfo))
            {
                try
                {
                    info = JsonSerializer.Deserialize<AndroidDeviceInfo>(AppConfig.AndroidDeviceInfo, DeviceFpJsonContext.Default.AndroidDeviceInfo);
                }
                catch { }
            }
            info ??= AndroidDeviceInfo.CreateRandom();
            info.BbsDeviceId = deviceId;
            AppConfig.AndroidDeviceInfo = JsonSerializer.Serialize(info, DeviceFpJsonContext.Default.AndroidDeviceInfo);
            _deviceInfo = info;
            _client.DeviceId = deviceId;
            _logger.LogInformation("Device id (x-rpc-device_id) set to {DeviceId}.", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save the device id");
            return false;
        }
    }




    #endregion




    #region 与米游社工具箱共用鉴权数据




    /// <summary>
    /// 当前账号对应的米游社 Cookie。LToken 缺失时返回 null。
    /// </summary>
    public string? GetToolboxCookie() => TokenSet?.CreateToolboxCookie();




    /// <summary>
    /// 把通行证令牌同步给米游社工具箱。
    /// 令牌没有变化时不会发出任何请求，账号首次同步时才会写入账号与游戏角色。
    /// </summary>
    public async Task SyncToToolboxAsync(PassportTokenSet? tokenSet = null, CancellationToken cancellationToken = default)
    {
        try
        {
            tokenSet ??= TokenSet;
            string? cookie = tokenSet?.CreateToolboxCookie();
            if (tokenSet is null || string.IsNullOrWhiteSpace(cookie) || tokenSet.Aid <= 0)
            {
                return;
            }
            if (tokenSet.Region is not ServerRegion.miHoYo)
            {
                // 通行证仅支持国服，国际服工具箱不受影响。
                return;
            }
            var user = GameRecordRoleService.GetRecordUser(tokenSet.Aid, false);
            // 还需要补角色行的两种情况：账号行不存在（首次同步），或账号行已存在但这次是**全新库**、
            // 一行角色都还没有。后者以前会被下面的分支跳过，于是既不写角色、也不发角色变化消息，
            // 登录后右上角的便笺 / 签到入口就一直停在 Collapsed，要手动切一下游戏才出现。
            bool needRoleSync = user is null || !HasAnyGameRecordRole(tokenSet.Aid);
            if (user is null)
            {
                user = await AddOrUpdateRecordUserAsync(tokenSet, cookie, cancellationToken);
                if (user is null)
                {
                    // 拿不到工具箱账号信息就不写角色行；等下次登录 / 刷新再补。
                    return;
                }
                _logger.LogInformation("Passport account synced to miyoushe toolbox (new user).");
            }
            else if (!string.Equals(user.Cookie, cookie, StringComparison.Ordinal))
            {
                // Cookie 变化时只更新数据库，不额外请求接口。
                GameRecordRoleService.UpdateRecordUserCookie(tokenSet.Aid, false, cookie);
                user.Cookie = cookie;
                _logger.LogInformation("Passport account cookie updated in miyoushe toolbox.");
            }
            if (needRoleSync)
            {
                // 角色行写完之后再发消息：订阅方收到后会重新读取角色并重算入口可见性。
                await _gameRecordRoleService.UpdateGameRolesByCookieAsync(cookie, tokenSet.Aid, cancellationToken);
                WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
                _logger.LogInformation("Passport account roles synced to miyoushe toolbox.");
            }
            // 米游社昵称随工具箱账号信息一起获得，用于界面展示，不额外请求接口。
            if (!string.IsNullOrWhiteSpace(user.Nickname) && tokenSet.Nickname != user.Nickname)
            {
                tokenSet.Nickname = user.Nickname;
                SaveTokenSet(tokenSet);
            }
            SyncAccountToPassportUserInfo(tokenSet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync passport account to miyoushe toolbox");
        }
    }




    /// <summary>
    /// 拉取工具箱账号信息（米游社昵称 / 头像）并写入 <c>GameRecordUser</c>，返回写库后的账号。
    /// 上游 0.19 不再用这张表，但主分支的账号池需要它来展示昵称头像、以及记录工具箱 Cookie。
    /// </summary>
    private async Task<GameRecordUser?> AddOrUpdateRecordUserAsync(PassportTokenSet tokenSet, string cookie, CancellationToken cancellationToken)
    {
        try
        {
            GameRecordUser user = await _client.GetBbsUserInfoAsync(tokenSet.Region, tokenSet.Aid, cancellationToken);
            // 通行证账号 ID 与工具箱账号 UID 是同一个值，强制对齐，避免同一个账号出现两行。
            user.Uid = tokenSet.Aid;
            user.IsHoyolab = tokenSet.Region is ServerRegion.HoYoverse;
            user.Cookie = cookie;
            GameRecordRoleService.SaveRecordUser(user);
            return user;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get the toolbox user info of the passport account (aid: {aid})", tokenSet.Aid);
            return null;
        }
    }




    /// <summary>
    /// 该账号是否已经有任何一行工具箱角色（<c>GameRecordRole</c>）。
    /// <para>
    /// 用来判断登录后要不要补写角色并发一次 <see cref="GameRecordRoleChangedMessage"/>：
    /// 全新库里账号行可能存在但一行角色都没有，此时右上角的便笺 / 签到入口不会出现。
    /// 只读数据库，不发请求。
    /// </para>
    /// </summary>
    /// <param name="aid">通行证账号 ID。</param>
    /// <returns>已有任意角色行时返回 true。</returns>
    private bool HasAnyGameRecordRole(long aid)
    {
        if (aid <= 0)
        {
            return false;
        }
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            var cookies = dapper.Query<string>("SELECT Cookie FROM GameRecordRole WHERE Cookie IS NOT NULL AND Cookie <> '';");
            // 用项目自带的账号 ID 解析（认得 ltuid / account_id / stuid 等写法），比字符串匹配可靠。
            return cookies.Any(cookie => GameRecordRoleService.GetCookieAccountId(cookie) == aid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Check the toolbox roles of the passport account (aid: {aid})", aid);
            return false;
        }
    }




    #endregion




    #region 多账号记录与切换




    /// <summary>账号列表的缓存，任何写操作后都会失效。</summary>
    private IReadOnlyList<PassportAccountItem>? _accountItems;


    /// <summary>
    /// 登录已经失效的账号标识。由 <see cref="RefreshAllAccountsAsync"/> / <see cref="VerifyAllAccountsAsync"/>
    /// 维护，账号中心据此在对应账号上显示警告红点；本地重新登录成功后自动移除。
    /// </summary>
    private readonly HashSet<string> _expiredAccountKeys = new();




    /// <summary>
    /// 已保存的全部账号（账号池），最近登录的排在前面。只读数据库，不会产生任何网络请求。
    /// <para>
    /// 当前账号一定会出现在列表里：旧版本只保存了一个账号，第一次读取时会把它补成一条记录。
    /// </para>
    /// </summary>
    public IReadOnlyList<PassportAccountItem> GetAccounts()
    {
        try
        {
            var current = TokenSet;
            string? currentKey = current?.HasStoken is true ? PassportAccountRecord.GetKey(current) : null;
            var records = QueryOrderedAccountRecords();
            if (currentKey is not null && records.All(x => x.Key != currentKey))
            {
                // 旧版本只保存了当前账号，这里把它补进账号列表。
                TrySaveAccountRecord(current!);
                records = QueryOrderedAccountRecords();
            }
            var list = new List<PassportAccountItem>(records.Count);
            foreach (var record in records)
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet is null)
                {
                    continue;
                }
                bool isExpired;
                lock (_lock)
                {
                    isExpired = _expiredAccountKeys.Contains(record.Key);
                }
                list.Add(new PassportAccountItem
                {
                    Key = record.Key,
                    DisplayName = tokenSet.DisplayName ?? Lang.PassportLogin_UnknownAccount,
                    Detail = GetAccountIdText(tokenSet),
                    AvatarUrl = GetAccountAvatarUrl(tokenSet.Aid) ?? PassportAccountItem.DefaultAvatarUrl,
                    Pendant = GetAccountPendant(tokenSet.Aid, tokenSet.Region),
                    Region = tokenSet.Region,
                    LoginName = string.IsNullOrWhiteSpace(tokenSet.AccountName) ? tokenSet.Mid : tokenSet.AccountName,
                    IsExpired = isExpired,
                });
            }
            _accountItems = list;
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get passport account list");
            return _accountItems ?? [];
        }
    }




    /// <summary>
    /// 账号池里的账号，供启动页的切换器使用：昵称、头像、UID 取这个账号在这个游戏里的角色资料，
    /// 还没有角色的账号退回账号本身的资料。
    /// <para>
    /// 这份列表和游戏注册表无关——开关关闭时国服的账号切换只切米游社账号，
    /// 登录数据只通过启动参数传给游戏。
    /// </para>
    /// </summary>
    public List<GameAccountItem> GetPassportGameAccounts(GameBiz gameBiz)
    {
        var list = new List<GameAccountItem>();
        try
        {
            if (string.IsNullOrWhiteSpace(gameBiz.Value))
            {
                return list;
            }
            var roles = GameRecordRoleService.GetGameRoles(gameBiz);
            var current = TokenSet;
            string? currentKey = current?.HasStoken is true ? PassportAccountRecord.GetKey(current) : null;
            foreach (var record in QueryOrderedAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is not true)
                {
                    continue;
                }
                var role = roles.FirstOrDefault(x => x.Uid > 0 && GameRecordRoleService.GetCookieAccountId(x.Cookie) == tokenSet.Aid);
                list.Add(new GameAccountItem
                {
                    GameBiz = gameBiz,
                    Uid = role?.Uid ?? 0,
                    Name = role?.Nickname ?? tokenSet.DisplayName ?? Lang.PassportLogin_UnknownAccount,
                    HeadIcon = role?.HeadIcon ?? GetAccountAvatarUrl(tokenSet.Aid),
                    PassportAccountKey = record.Key,
                    IsSaved = true,
                    IsCurrent = record.Key == currentKey,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game accounts of passport ({biz})", gameBiz);
        }
        return list;
    }




    /// <summary>
    /// 让当前会话跟随游戏：打开某个游戏时，用这个游戏记录过的账号。
    /// <para>
    /// 一个通行证在一个游戏里只对应一个游戏账号，所以每个游戏各记一个账号即可；
    /// 游戏还没记录过账号时，把当前账号记到它名下，行为与以前保持一致。
    /// </para>
    /// <para>
    /// 「基于注册表的游戏账号切换」打开时整条线不参与：游戏账号由游戏客户端自己记住的那份数据决定，
    /// 米游社这边不跟着游戏换账号，也不去读写注册表。
    /// </para>
    /// </summary>
    public async Task UseAccountForGameAsync(GameBiz gameBiz, CancellationToken cancellationToken = default)
    {
        try
        {
            if (AppConfig.EnableRegistryGameAccountSwitcher)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(gameBiz.Value))
            {
                return;
            }
            var current = TokenSet;
            string? currentKey = current?.HasStoken is true ? PassportAccountRecord.GetKey(current) : null;
            string? key = AppConfig.GetCurrentPassportAccountKey(gameBiz);
            if (string.IsNullOrWhiteSpace(key))
            {
                if (currentKey is not null)
                {
                    AppConfig.SetCurrentPassportAccountKey(gameBiz, currentKey);
                }
                return;
            }
            if (key == currentKey)
            {
                return;
            }
            var tokenSet = QueryAccountRecords().Where(x => x.Key == key).Select(DecryptTokenSet).FirstOrDefault(x => x?.HasStoken is true);
            if (tokenSet is null)
            {
                // 记录的账号已经不在了，退回当前账号。
                if (currentKey is not null)
                {
                    AppConfig.SetCurrentPassportAccountKey(gameBiz, currentKey);
                }
                return;
            }
            if (current is not null)
            {
                TrySaveAccountRecord(current);
            }
            SaveTokenSet(tokenSet);
            await SyncToToolboxAsync(tokenSet, cancellationToken);
            WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
            _logger.LogInformation("Use passport account of {biz} (aid: {aid}).", gameBiz, tokenSet.Aid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Use passport account for game ({biz})", gameBiz);
        }
    }




    /// <summary>
    /// 按启动预设里保存的账号切换该区服的当前账号；账号已经不存在时**自动回退**：
    /// 什么都不切，保持账号选择器里当前选中的账号。
    /// <para>
    /// 返回 false 表示这个 key 已经失效，调用方应该把它从预设里清掉。
    /// 启用「基于注册表的游戏账号切换」时整项不生效（返回 true，调用方照常保留记录）。
    /// </para>
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <param name="key">预设里保存的账号 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> TryApplyProfileAccountAsync(GameBiz gameBiz, string? key, CancellationToken cancellationToken = default)
    {
        if (AppConfig.EnableRegistryGameAccountSwitcher)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }
        if (!QueryAccountRecords().Any(x => x.Key == key))
        {
            return false;
        }
        await UsePassportAccountAsync(gameBiz, key, cancellationToken);
        // 账号选择器 / 账号中心要跟着刷新：预设换号时它们不一定能收到角色变更消息。
        WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(IsLoggedIn));
        return true;
    }




    /// <summary>
    /// 从列表里删除一个账号。删掉的正好是当前账号时，如果还有其它账号就直接顺延过去，
    /// 都没有了才回到登录界面——避免出现“账号还在列表里，界面却退不回去”的死角。
    /// </summary>
    public bool RemoveAccount(string key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }
            PassportAccountRecord? record = DatabaseService.GetPassportAccount(key);
            PassportTokenSet? tokenSet = record is null ? null : DecryptTokenSet(record);
            if (!DatabaseService.DeletePassportAccount(key))
            {
                return false;
            }
            _accountItems = null;
            lock (_lock)
            {
                _expiredAccountKeys.Remove(key);
            }
            RemovePassportUserInfoRow(tokenSet);
            var current = TokenSet;
            if (current?.HasStoken is true && PassportAccountRecord.GetKey(current) == key)
            {
                var next = QueryOrderedAccountRecords().Select(DecryptTokenSet).FirstOrDefault(x => x?.HasStoken is true);
                SaveTokenSet(next);
                if (next is null)
                {
                    AppConfig.SetLastPassportUserAid(ServerRegion.miHoYo, 0);
                }
                WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
            }
            // 清掉各游戏里指向这个账号的选择，下次打开游戏会落到当前账号上。
            foreach (GameBiz gameBiz in AllGameBizs)
            {
                if (AppConfig.GetCurrentPassportAccountKey(gameBiz) == key)
                {
                    AppConfig.SetCurrentPassportAccountKey(gameBiz, null);
                }
            }
            if (tokenSet?.HasStoken is true)
            {
                _ = LogoutFromServerAsync(tokenSet);
            }
            _logger.LogInformation("Removed a passport account from the account list.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remove passport account");
            return false;
        }
    }




    /// <summary>
    /// 取拥有某个游戏角色的通行证账号的 SToken 与 Mid，供需要 stoken 的接口使用
    /// （例如用 stoken 换抽卡 authkey）。
    /// <para>
    /// 只读：不切换当前账号、不续期 LToken、不改工具箱 Cookie。工具箱 Cookie 里只有派生令牌
    /// （ltoken / cookie_token），不含 stoken，所以这类接口必须从这里取。
    /// </para>
    /// </summary>
    /// <param name="gameBiz">游戏区服。</param>
    /// <param name="uid">游戏 UID。</param>
    /// <returns>该角色所属账号的 SToken 与 Mid；找不到、或账号没有 SToken V2 时返回 null。</returns>
    public (string Stoken, string Mid)? GetStokenForGameRole(GameBiz gameBiz, long uid)
    {
        try
        {
            if (uid <= 0 || string.IsNullOrWhiteSpace(gameBiz.Value))
            {
                return null;
            }
            foreach (var record in QueryAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet is null || !tokenSet.HasStoken || string.IsNullOrWhiteSpace(tokenSet.Mid))
                {
                    continue;
                }
                if (GetGameRoleOfAccount(tokenSet, gameBiz)?.Uid == uid)
                {
                    return (tokenSet.Stoken!, tokenSet.Mid);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get stoken for game role ({biz}, {uid})", gameBiz, uid);
            return null;
        }
    }




    /// <summary>
    /// 取出账号池里某个账号的令牌，按需校验 / 续期。只读写这个账号自己的记录，**不切换当前会话**：
    /// 申请游戏授权票据只需要 stoken，不能用「先切会话」的方式做，否则会把预设选中的账号顶掉。
    /// </summary>
    /// <param name="key">账号池里的账号标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌可用时返回它；账号不存在或已经失效时返回 null。</returns>
    public async Task<PassportTokenSet?> GetAccountTokenSetAsync(string? key, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }
            var tokenSet = QueryAccountRecords()
                .Where(x => x.Key == key)
                .Select(DecryptTokenSet)
                .FirstOrDefault(x => x?.HasStoken is true);
            if (tokenSet is null)
            {
                return null;
            }
            var now = DateTimeOffset.Now;
            bool needVerify = tokenSet.NeedVerifyStoken(now);
            bool needDerived = tokenSet.NeedRefreshDerivedToken(now);
            if (needVerify || needDerived)
            {
                await PrepareDeviceAsync(cancellationToken);
                if (!await RefreshAccountAsync(tokenSet, needVerify, needDerived, cancellationToken))
                {
                    return null;
                }
            }
            return tokenSet;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get account token set ({key})", key);
            return null;
        }
    }




    /// <summary>
    /// 用户在游戏账号切换器里选了这个游戏里的某个游戏账号，让通行证会话跟着它走。
    /// 自动登录用的是通行证账号，不跟着走的话，界面上的选择和实际登录的账号会对不上。
    /// <para>
    /// 「基于注册表的游戏账号切换」打开时什么都不做：那条线里没有一个叫米游社账号的东西参与登录，
    /// 会话跟过去只会让两边对不上。
    /// </para>
    /// </summary>
    public async Task UsePassportAccountOfGameUidAsync(GameBiz gameBiz, long uid, CancellationToken cancellationToken = default)
    {
        try
        {
            if (AppConfig.EnableRegistryGameAccountSwitcher)
            {
                return;
            }
            if (uid <= 0)
            {
                return;
            }
            var target = QueryAccountRecords()
                .Select(DecryptTokenSet)
                .FirstOrDefault(x => x?.HasStoken is true && GetGameRoleOfAccount(x, gameBiz)?.Uid == uid);
            if (target is null)
            {
                // 这个游戏账号不属于任何一个已保存的通行证账号，保持现状。
                return;
            }
            await UsePassportAccountAsync(gameBiz, PassportAccountRecord.GetKey(target), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Use passport account of game uid ({uid})", uid);
        }
    }




    /// <summary>
    /// 把当前会话切到账号池里的某个账号，并把它记成这个游戏要用的账号。
    /// <para>
    /// 只换米游社这边的凭据（会话、工具箱 Cookie、该换的 LToken），不碰游戏注册表：
    /// 启动游戏时用这个账号签授权票据，通过启动参数交给游戏。
    /// </para>
    /// </summary>
    public async Task UsePassportAccountAsync(GameBiz gameBiz, string? key, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            var current = TokenSet;
            string? currentKey = current?.HasStoken is true ? PassportAccountRecord.GetKey(current) : null;
            if (key == currentKey)
            {
                AppConfig.SetCurrentPassportAccountKey(gameBiz, key);
                // 会话没变，但「最近选择的角色」可能还停在别的账号上，顺手对齐一次。
                ApplyAccountGameRole(gameBiz, current!);
                return;
            }
            var target = QueryAccountRecords()
                .Where(x => x.Key == key)
                .Select(DecryptTokenSet)
                .FirstOrDefault(x => x?.HasStoken is true);
            if (target is null)
            {
                return;
            }
            AppConfig.SetCurrentPassportAccountKey(gameBiz, key);
            if (current is not null)
            {
                TrySaveAccountRecord(current);
            }
            SaveTokenSet(target);
            // 切过去马上就要用这个账号的工具箱 Cookie，LToken 该换就先换一份。
            if (target.NeedRefreshDerivedToken(DateTimeOffset.Now))
            {
                await RefreshAccountAsync(target, needVerify: false, needDerived: true, cancellationToken);
            }
            await SyncToToolboxAsync(target, cancellationToken);
            // 让便笺 / 签到 / 公告这些按「最近选择的角色」解析的消费者跟着账号切换。
            // 必须放在工具箱同步之后：新账号的角色就是那一步刚写进数据库的。
            ApplyAccountGameRole(gameBiz, target);
            WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
            _logger.LogInformation("Switched passport account (biz: {biz}).", gameBiz);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Use passport account ({key})", key);
        }
    }




    /// <summary>
    /// 把这个通行证账号在该游戏里的角色记为「最近选择的角色」：每日便笺、签到、公告等
    /// 按角色解析的地方因此跟着账号选择器一起切换。
    /// <para>
    /// 同一通行证在该游戏有多个角色时，尽量保留用户当前选中的那个（它属于这个账号就不动），
    /// 否则取第一个。已经是这个角色时不重复写设置。
    /// </para>
    /// </summary>
    private void ApplyAccountGameRole(GameBiz gameBiz, PassportTokenSet tokenSet)
    {
        try
        {
            var roles = GameRecordRoleService.GetGameRoles(gameBiz)
                .Where(x => x.Uid > 0 && GameRecordRoleService.GetCookieAccountId(x.Cookie) == tokenSet.Aid)
                .ToList();
            if (roles.Count == 0)
            {
                return;
            }
            long lastUid = GameRecordRoleService.GetLastSelectGameRecordRoleOrTheFirstOne(gameBiz)?.Uid ?? 0;
            GameRecordRole role = roles.FirstOrDefault(x => x.Uid == lastUid) ?? roles[0];
            if (role.Uid == lastUid)
            {
                return;
            }
            GameRecordRoleService.SetLastSelectGameRecordRole(gameBiz, role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply account game role ({biz})", gameBiz);
        }
    }




    /// <summary>
    /// 把当前会话切回登录前的账号。账号中心登录新账号只是往账号池里加一条记录，
    /// 不应该把预设 / 账号选择器正在用的账号顶掉，登录成功后用它恢复原来的会话。
    /// <para>
    /// 从账号池里重新读一份令牌（而不是用登录前的内存副本），避免覆盖期间已经刷新过的令牌；
    /// 账号已经不存在或没有有效令牌时什么都不做。
    /// </para>
    /// </summary>
    /// <param name="key">登录前的账号标识。</param>
    public void RestoreAccountSession(string? key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            var current = TokenSet;
            if (current?.HasStoken is true && PassportAccountRecord.GetKey(current) == key)
            {
                return;
            }
            var tokenSet = QueryAccountRecords()
                .Where(x => x.Key == key)
                .Select(DecryptTokenSet)
                .FirstOrDefault(x => x?.HasStoken is true);
            if (tokenSet is null)
            {
                return;
            }
            SaveTokenSet(tokenSet);
            _logger.LogInformation("Restored the previous passport account session after adding an account.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore passport account session ({key})", key);
        }
    }




    /// <summary>
    /// 当前通行证账号在某个游戏里的角色。绝区零这类游戏的注册表里只有一份登录凭据、
    /// 读不到 uid，界面上只能按当前通行证账号的角色来显示这个游戏账号。
    /// </summary>
    public GameRecordRole? GetCurrentGameRole(GameBiz gameBiz)
    {
        try
        {
            var tokenSet = TokenSet;
            return tokenSet?.HasStoken is true ? GetGameRoleOfAccount(tokenSet, gameBiz) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get current game role ({biz})", gameBiz);
            return null;
        }
    }




    /// <summary>
    /// 首页米游社服务（每日便笺、签到、公告等）当前应该使用的游戏角色。
    /// <para>
    /// 国服登录后跟着当前通行证账号走：账号在这个游戏没有角色时返回 null（入口收起），
    /// 不会退回到别的账号的角色上。国际服、B 服或未登录时沿用原来的「最近选择的角色」。
    /// </para>
    /// <para>
    /// B 服的角色在工具箱里按同名游戏的国服区服存，这里统一折算一次。
    /// </para>
    /// </summary>
    public GameRecordRole? GetCurrentAccountGameRole(GameBiz gameBiz)
    {
        try
        {
            GameBiz roleBiz = gameBiz.Server is "bilibili" ? $"{gameBiz.Game}_cn" : gameBiz;
            if (gameBiz.IsChinaServer() && IsLoggedIn)
            {
                var tokenSet = TokenSet;
                return tokenSet?.HasStoken is true ? GetGameRoleOfAccount(tokenSet, roleBiz) : null;
            }
            return GameRecordRoleService.GetLastSelectGameRecordRoleOrTheFirstOne(roleBiz);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get current account game role ({biz})", gameBiz);
            return null;
        }
    }




    /// <summary>
    /// 找回某个通行证账号在当前游戏里对应的角色。通行证 ID 和游戏内 UID 不是同一套编号，
    /// 唯一的对应关系来自米游社工具箱里的角色数据。
    /// <para>
    /// 角色的 Cookie 会随令牌刷新而变化，但其中的 account_id 始终是这个通行证账号，
    /// 所以按它来对应，而不是整段 Cookie。
    /// </para>
    /// </summary>
    private GameRecordRole? GetGameRoleOfAccount(PassportTokenSet tokenSet, GameBiz gameBiz)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameBiz.Value) || tokenSet.Aid <= 0)
            {
                return null;
            }
            return GameRecordRoleService.GetGameRoles(gameBiz)
                .FirstOrDefault(x => x.Uid > 0 && GameRecordRoleService.GetCookieAccountId(x.Cookie) == tokenSet.Aid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game role of passport account");
            return null;
        }
    }




    /// <summary>界面上的账号标识：米游社 ID，取不到时退回通行证 ID。</summary>
    private static string? GetAccountIdText(PassportTokenSet tokenSet)
    {
        string? id = tokenSet.Aid > 0 ? tokenSet.Aid.ToString() : tokenSet.Mid;
        return string.IsNullOrWhiteSpace(id) ? null : string.Format(Lang.PassportLogin_AccountId, id);
    }




    /// <summary>账号头像取自米游社工具箱里同一个账号的资料，读不到时界面会退回到通用图标。</summary>
    private string? GetAccountAvatarUrl(long aid)
    {
        if (aid <= 0)
        {
            return null;
        }
        return GameRecordRoleService.GetRecordUser(aid, false)?.AvatarUrl
            ?? GameRecordRoleService.GetRecordUser(aid, true)?.AvatarUrl;
    }




    /// <summary>账号的头像框取自米游社工具箱里同一个账号的资料，没有时返回 null。</summary>
    private static string? GetAccountPendant(long aid, ServerRegion region)
    {
        if (aid <= 0)
        {
            return null;
        }
        return GameRecordRoleService.GetRecordUser(aid, region is ServerRegion.HoYoverse)?.Pendant;
    }




    /// <summary>
    /// 把账号池里每个账号的昵称换成工具箱里最新的那份。只读本机数据库，不会发请求。
    /// </summary>
    public void SyncAccountNamesFromToolbox()
    {
        try
        {
            bool changed = false;
            foreach (var record in QueryOrderedAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is not true || tokenSet.Aid <= 0)
                {
                    continue;
                }
                string? nickname = GameRecordRoleService.GetRecordUser(tokenSet.Aid, false)?.Nickname;
                if (!string.IsNullOrWhiteSpace(nickname) && nickname != tokenSet.Nickname)
                {
                    tokenSet.Nickname = nickname;
                    SaveTokenSetOrRecord(tokenSet);
                    changed = true;
                }
            }
            if (changed)
            {
                _accountItems = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync account names from toolbox");
        }
    }




    private List<PassportAccountRecord> QueryAccountRecords()
    {
        return DatabaseService.GetPassportAccounts();
    }




    /// <summary>用户拖动过的账号顺序（账号标识）。</summary>
    private static List<string> GetSavedAccountOrder()
    {
        return AppConfig.PassportAccountOrder?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
    }




    /// <summary>
    /// 保存账号列表的拖动排序结果。没有出现在列表里的账号（比如之后新登录的）排在最后。
    /// </summary>
    public void SaveAccountOrder(IEnumerable<string> keys)
    {
        try
        {
            AppConfig.PassportAccountOrder = string.Join(',', keys.Where(x => !string.IsNullOrWhiteSpace(x)));
            _accountItems = null;
            // 顺序变了，所有展示账号列表的地方都得刷新。
            WeakReferenceMessenger.Default.Send(new PassportAccountChangedMessage(IsLoggedIn));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save passport account order");
        }
    }




    /// <summary>
    /// 把一个新账号的 key 追加到当前生效顺序的末尾（现有记录按它们现在的显示顺序排在前面）。
    /// </summary>
    private void AppendAccountOrder(string key)
    {
        var order = QueryOrderedAccountRecords().Select(x => x.Key).Where(x => x != key).ToList();
        order.Add(key);
        SaveAccountOrder(order);
    }




    /// <summary>按用户拖动过的顺序读取账号记录，没有排过的账号按最近登录时间排在最后。</summary>
    private List<PassportAccountRecord> QueryOrderedAccountRecords()
    {
        var records = QueryAccountRecords();
        var order = GetSavedAccountOrder();
        if (order.Count == 0)
        {
            return records;
        }
        var index = new Dictionary<string, int>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            index.TryAdd(order[i], i);
        }
        return records.OrderBy(x => index.TryGetValue(x.Key, out int i) ? i : int.MaxValue).ThenByDescending(x => x.Time).ToList();
    }




    private void TrySaveAccountRecord(PassportTokenSet tokenSet)
    {
        try
        {
            SaveAccountRecord(tokenSet);
        }
        catch (Exception ex)
        {
            // 账号列表是附加功能，写不进去也不能影响登录本身。
            _logger.LogError(ex, "Save passport account record");
        }
    }




    private void SaveAccountRecord(PassportTokenSet tokenSet)
    {
        string json = JsonSerializer.Serialize(tokenSet, PassportJson.Options);
        string? protectedJson = DpapiHelper.Protect(json);
        if (string.IsNullOrWhiteSpace(protectedJson))
        {
            return;
        }
        var record = new PassportAccountRecord
        {
            Key = PassportAccountRecord.GetKey(tokenSet),
            Region = tokenSet.Region,
            Aid = tokenSet.Aid,
            Mid = tokenSet.Mid,
            Nickname = tokenSet.Nickname,
            AccountName = tokenSet.AccountName,
            LoginMethod = tokenSet.LoginMethod,
            LoginTime = tokenSet.LoginTime,
            Token = Encoding.UTF8.GetBytes(protectedJson),
            Time = DateTimeOffset.Now,
        };
        // 新登录的账号排到已有账号后面：先看这条 key 是不是新的，是新的就追加到当前顺序末尾。
        bool isNewAccount = DatabaseService.SavePassportAccount(record);
        if (isNewAccount)
        {
            AppendAccountOrder(record.Key);
        }
        _accountItems = null;
    }




    private PassportTokenSet? DecryptTokenSet(PassportAccountRecord record)
    {
        try
        {
            string? json = DpapiHelper.Unprotect(Encoding.UTF8.GetString(record.Token));
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PassportTokenSet>(json, PassportJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decrypt passport account token");
            return null;
        }
    }




    #endregion




    #region 与上游 PassportService 共存




    /// <summary>
    /// 启动时调用一次（幂等）：加载当前账号、迁移旧配置、把账号池与上游 <c>PassportUserInfo</c> 表对齐。
    /// <para>
    /// 必须在主窗口创建之前调用，否则 MainView 读到的还是旧账号列表、自动登录也找不到账号。
    /// 整个过程只读本机数据库与配置，不产生网络请求。
    /// </para>
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }
            try
            {
                _ = TokenSet;
                ImportFromPassportUserInfo();
                BackfillGameRecordRoleAid();
                SyncToPassportUserInfo();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initialize passport account service");
            }
            finally
            {
                _initialized = true;
            }
        }
    }




    /// <summary>
    /// 把账号池里的国服账号同步进上游的 <c>PassportUserInfo</c> 表。
    /// <para>
    /// 上游 0.19 的 MainView 账号浮窗、<see cref="GameLauncherService"/> 的自动登录、
    /// <see cref="GameRecordRoleService"/> 的 Cookie 派生都读这张表；不写它的话，
    /// 升到 0.19 之后这些已有功能会认为没有登录过。
    /// </para>
    /// </summary>
    public void SyncToPassportUserInfo()
    {
        try
        {
            foreach (var record in QueryAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is true)
                {
                    SyncAccountToPassportUserInfo(tokenSet);
                }
            }
            var current = TokenSet;
            if (current?.HasStoken is true && current.Aid > 0)
            {
                AppConfig.SetLastPassportUserAid(current.Region, current.Aid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync passport accounts to PassportUserInfo");
        }
    }




    /// <summary>
    /// 把一个账号写到上游的 <c>PassportUserInfo</c> 表（按 Region + Aid 覆盖，保留已有的昵称 / 头像）。
    /// 只在有 mid 的 V2 会话下同步：上游这张表要求 Mid 非空。
    /// </summary>
    private void SyncAccountToPassportUserInfo(PassportTokenSet? tokenSet)
    {
        try
        {
            if (tokenSet is null || !tokenSet.HasStoken || tokenSet.Aid <= 0 || string.IsNullOrWhiteSpace(tokenSet.Mid))
            {
                return;
            }
            PassportUserInfoModel? existing = GetUpstreamPassportUserInfo(tokenSet.Region, tokenSet.Aid);
            GameRecordUser? user = tokenSet.Region is ServerRegion.miHoYo ? GameRecordRoleService.GetRecordUser(tokenSet.Aid, false) : null;
            var model = new PassportUserInfoModel
            {
                Region = tokenSet.Region,
                Aid = tokenSet.Aid,
                Mid = tokenSet.Mid!,
                Stoken = tokenSet.Stoken!,
                AccountName = string.IsNullOrWhiteSpace(tokenSet.AccountName) ? existing?.AccountName ?? "" : tokenSet.AccountName,
                Nickname = !string.IsNullOrWhiteSpace(tokenSet.Nickname) ? tokenSet.Nickname : user?.Nickname ?? existing?.Nickname ?? "",
                AvatarUrl = existing?.AvatarUrl ?? user?.AvatarUrl ?? "",
                Pendant = existing?.Pendant,
            };
            PassportService.SavePassportUserInfo(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync a passport account to PassportUserInfo");
        }
    }




    /// <summary>
    /// 上游 0.19 的库里已经有、但账号池里还没有的国服账号，导入账号池。
    /// <para>
    /// 只导入能解密、并且令牌校验成功过的国服行；国际服（HoYoverse）账号留给界面阶段再导入。
    /// 导入的账号没有派生令牌，第一次切换时按需换取。
    /// </para>
    /// </summary>
    public void ImportFromPassportUserInfo()
    {
        try
        {
            var existingAids = QueryAccountRecords()
                .Where(x => x.Region is ServerRegion.miHoYo && x.Aid > 0)
                .Select(x => x.Aid)
                .ToHashSet();
            foreach (var info in PassportService.GetPassportUserInfos(ServerRegion.miHoYo))
            {
                if (info.Aid <= 0 || info.ErrorType is not PassportErrorType.Success || string.IsNullOrWhiteSpace(info.Stoken))
                {
                    continue;
                }
                if (existingAids.Contains(info.Aid))
                {
                    continue;
                }
                var tokenSet = new PassportTokenSet
                {
                    Region = ServerRegion.miHoYo,
                    Stoken = info.Stoken,
                    StokenVersion = info.Stoken.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1,
                    Mid = info.Mid,
                    Aid = info.Aid,
                    LoginTime = DateTimeOffset.Now,
                    LoginMethod = "import",
                    Nickname = info.Nickname,
                };
                if (!tokenSet.HasStoken)
                {
                    continue;
                }
                SaveAccountRecord(tokenSet);
                _logger.LogInformation("Imported a passport account from PassportUserInfo (aid: {aid}).", info.Aid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import passport accounts from PassportUserInfo");
        }
    }




    /// <summary>
    /// 把上游 HYP 登录（<see cref="HypLoginWindow"/>）得到的账号写进账号池，返回账号标识。
    /// <para>
    /// 【界面阶段调用】现在 MainView 的 HYP 登录只写 <c>PassportUserInfo</c>，账号中心迁移完成后
    /// 由登录成功的回调调用这里，把账号同时放进账号池（国际服账号也支持，Region 原样保存）。
    /// 只入库，不切换当前会话、不发网络请求；国服账号会顺手把工具箱 Cookie 同步一次。
    /// </para>
    /// </summary>
    /// <param name="model">HYP 登录得到的账号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>账号池里的账号标识；参数无效时返回 null。</returns>
    public async Task<string?> ImportPassportUserInfoAsync(PassportUserInfoModel? model, CancellationToken cancellationToken = default)
    {
        try
        {
            if (model is null || model.Aid <= 0 || string.IsNullOrWhiteSpace(model.Stoken))
            {
                return null;
            }
            var tokenSet = new PassportTokenSet
            {
                Region = model.Region,
                Stoken = model.Stoken,
                StokenVersion = model.Stoken.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1,
                Mid = model.Mid,
                Aid = model.Aid,
                AccountName = model.AccountName,
                Nickname = model.Nickname,
                LoginTime = DateTimeOffset.Now,
                LoginMethod = "hyp",
            };
            SaveTokenSetOrRecord(tokenSet);
            if (tokenSet.Region is ServerRegion.miHoYo)
            {
                await SyncToToolboxAsync(tokenSet, cancellationToken);
            }
            return PassportAccountRecord.GetKey(tokenSet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import a HYP passport account into the account list");
            return null;
        }
    }




    /// <summary>读取上游 <c>PassportUserInfo</c> 表里指定区域的那一行（按 Region + Aid 精确匹配）。</summary>
    private static PassportUserInfoModel? GetUpstreamPassportUserInfo(ServerRegion region, long aid)
    {
        try
        {
            return PassportService.GetPassportUserInfos(region).FirstOrDefault(x => x.Aid == aid);
        }
        catch
        {
            return null;
        }
    }




    /// <summary>删掉上游 <c>PassportUserInfo</c> 表里对应的账号行，让 MainView 不再显示它。</summary>
    private void RemovePassportUserInfoRow(PassportTokenSet? tokenSet)
    {
        try
        {
            if (tokenSet is null || tokenSet.Aid <= 0 || string.IsNullOrWhiteSpace(tokenSet.Mid))
            {
                return;
            }
            PassportService.DeletePassportUserInfo(new PassportUserInfoModel
            {
                Region = tokenSet.Region,
                Aid = tokenSet.Aid,
                Mid = tokenSet.Mid!,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete the passport user info of the account (aid: {aid})", tokenSet?.Aid);
        }
    }




    /// <summary>
    /// 给工具箱角色行补上 <c>Aid</c>（通行证账号 ID）。
    /// <para>
    /// 上游 v24 加这个列时把角色行清空了；本分支保留角色数据，升级上来的行 Aid 默认是 0。
    /// 补上之后，<see cref="GameRecordRoleService.UpdateGameRoleAsync"/> 在 Cookie 失效时才能找回通行证账号续期。
    /// </para>
    /// </summary>
    private void BackfillGameRecordRoleAid()
    {
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            var roles = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole WHERE Aid = 0 AND Cookie IS NOT NULL AND Cookie <> '';").ToList();
            foreach (var role in roles)
            {
                long aid = GameRecordRoleService.GetCookieAccountId(role.Cookie);
                if (aid <= 0)
                {
                    aid = PassportCookieParser.Parse(role.Cookie).Aid;
                }
                if (aid > 0)
                {
                    dapper.Execute("UPDATE GameRecordRole SET Aid = @aid WHERE GameBiz = @GameBiz AND Uid = @Uid;", new { aid, role.GameBiz, role.Uid });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backfill the account id of the game record roles");
        }
    }




    #endregion




    #region 旧配置迁移




    /// <summary>
    /// 把旧版本写在 config.ini / 注册表里的 SToken 迁移到数据库，并清理配置文件中的敏感信息。
    /// <para>
    /// 这是主分支在引入账号池之前用的存储位置；从更老的版本升级上来时，
    /// 数据库里的账号池还是空的，需要把单账号凭据引导进来一次。
    /// </para>
    /// </summary>
    private void MigrateLegacyConfiguration()
    {
        try
        {
            (string? stoken, string? mid) = ReadLegacyConfigurationStoken();
            if (_tokenSet is null && !string.IsNullOrWhiteSpace(stoken) && !string.IsNullOrWhiteSpace(mid))
            {
                string value = stoken;
                _tokenSet = new PassportTokenSet
                {
                    Region = ServerRegion.miHoYo,
                    Stoken = value,
                    StokenVersion = value.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1,
                    Mid = mid,
                    LoginTime = DateTimeOffset.Now,
                    LoginMethod = "legacy",
                };
                SaveTokenSet(_tokenSet);
                _logger.LogInformation("Migrated the legacy stoken from the configuration file to the database.");
            }
            if (!string.IsNullOrWhiteSpace(stoken) || !string.IsNullOrWhiteSpace(mid))
            {
                ClearLegacyConfigurationStoken();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migrate legacy passport configuration");
        }
    }




    /// <summary>从 config.ini 或注册表读取旧版本的 SToken / mid。</summary>
    private static (string? Stoken, string? Mid) ReadLegacyConfigurationStoken()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AppConfig.ConfigPath) && File.Exists(AppConfig.ConfigPath))
            {
                string text = File.ReadAllText(AppConfig.ConfigPath);
                string? stoken = Regex.Match(text, @"^stoken=(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
                string? mid = Regex.Match(text, @"^mid=(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
                return (NullIfEmpty(stoken), NullIfEmpty(mid));
            }
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StarwardRegistryKey);
            string? stokenValue = key?.GetValue("stoken") as string;
            string? midValue = key?.GetValue("mid") as string;
            return (NullIfEmpty(stokenValue?.Trim()), NullIfEmpty(midValue?.Trim()));
        }
        catch
        {
            return (null, null);
        }
    }




    /// <summary>把旧配置里的 SToken / mid 删掉：config.ini 重写一次（0.19 只写语言与用户目录），注册表直接删值。</summary>
    private static void ClearLegacyConfigurationStoken()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AppConfig.ConfigPath))
            {
                AppConfig.SaveConfiguration();
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StarwardRegistryKey, writable: true);
                key?.DeleteValue("stoken", throwOnMissingValue: false);
                key?.DeleteValue("mid", throwOnMissingValue: false);
            }
        }
        catch { }
    }




#if DEBUG
    private const string StarwardRegistryKey = @"Software\Starward.Debug";
#else
    private const string StarwardRegistryKey = @"Software\Starward";
#endif




    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;




    #endregion


}
