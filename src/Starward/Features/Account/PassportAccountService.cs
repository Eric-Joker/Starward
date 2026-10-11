using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.Passport;
using Starward.Features.Database;
using Starward.Features.GameAccount;
using Starward.Features.GameRecord;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// 同名的命名空间 Starward.Features.GameAccount 会遮住 GameAccount 这个类型，这里给它起个别名。
using GameAccountItem = Starward.Features.GameAccount.GameAccount;

namespace Starward.Features.Account;

/// <summary>
/// 通行证（国服）账号服务。
/// <para>
/// 负责令牌的保存、刷新，以及与米游社工具箱共用鉴权数据。
/// 设计原则：
/// 1. 所有米哈游接口请求都尽量只发必要的次数，设备指纹、账号信息等都会缓存复用；
/// 2. 令牌的失效特性在 <see cref="PassportTokenSet"/> 中静态区分，SToken 定期校验续期，
///    LToken / CookieToken 作为可再生令牌按需重新获取。
/// </para>
/// </summary>
internal class PassportAccountService
{


    private readonly ILogger<PassportAccountService> _logger;

    private readonly GameRecordService _gameRecordService;

    private readonly PassportClient _client;


    private readonly object _lock = new();


    private PassportTokenSet? _tokenSet;

    private bool _loaded;

    /// <summary>同一时间只允许一个刷新流程，避免重复请求。</summary>
    private Task<bool>? _refreshing;


    public PassportAccountService(ILogger<PassportAccountService> logger, GameRecordService gameRecordService, PassportClient client)
    {
        _logger = logger;
        _gameRecordService = gameRecordService;
        _client = client;
    }




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
                string? nickname = _gameRecordService.GetRecordUser(tokenSet.Aid, false)?.Nickname;
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
                    _tokenSet = System.Text.Json.JsonSerializer.Deserialize<PassportTokenSet>(json, PassportJson.Options);
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




    /// <summary>
    /// 把旧版本写在 config.ini / 注册表里的 SToken 迁移到数据库，并清理配置文件中的敏感信息。
    /// </summary>
    private void MigrateLegacyConfiguration()
    {
        try
        {
            if (_tokenSet is null && !string.IsNullOrWhiteSpace(AppConfig.stoken) && !string.IsNullOrWhiteSpace(AppConfig.mid))
            {
                string stoken = AppConfig.stoken;
                _tokenSet = new PassportTokenSet
                {
                    Stoken = stoken,
                    StokenVersion = stoken.StartsWith("v2_", StringComparison.Ordinal) ? 2 : 1,
                    Mid = AppConfig.mid,
                    LoginTime = DateTimeOffset.Now,
                    LoginMethod = "legacy",
                };
                SaveTokenSet(_tokenSet);
                _logger.LogInformation("Migrated legacy stoken from configuration file to database.");
            }
            if (!string.IsNullOrWhiteSpace(AppConfig.stoken) || !string.IsNullOrWhiteSpace(AppConfig.mid))
            {
                AppConfig.stoken = null;
                AppConfig.mid = null;
            }
            // 旧版开关不存在时会被读成“关闭”，并把默认开启的自动登录关掉，这里清掉被它写坏的值。
            AppConfig.RemoveValue(AppConfig.ObsoleteEnableAuthTicketLoginKey);
            AppConfig.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migrate legacy passport configuration");
        }
    }




    private void SaveTokenSet(PassportTokenSet? tokenSet)
    {
        _tokenSet = tokenSet;
        // 令牌属于敏感信息，落库前用 DPAPI 加密，只有当前 Windows 用户能够解密。
        string? json = tokenSet is null ? null : System.Text.Json.JsonSerializer.Serialize(tokenSet, PassportJson.Options);
        AppConfig.PassportTokenJson = DpapiHelper.Protect(json);
        // 当前账号同时也是账号列表里的一条记录，切换账号时直接从列表取回。
        if (tokenSet is not null)
        {
            TrySaveAccountRecord(tokenSet);
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
        }
    }




    /// <summary>
    /// 退出当前账号。账号记录会留在账号列表里（就是它保存的令牌），
    /// 之后可以直接切回来，不需要重新输入密码；要真正删掉请用 <see cref="RemoveAccount"/>。
    /// </summary>
    public void Logout()
    {
        try
        {
            SaveTokenSet(null);
            _logger.LogInformation("Passport account signed out, the account record is kept.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout passport account");
        }
    }




    #endregion




    #region 设备指纹




    /// <summary>
    /// 准备设备指纹。与米游社工具箱共用同一份指纹，避免重复申请。
    /// </summary>
    public async Task PrepareDeviceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _gameRecordService.UpdateDeviceFpAsync(cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceId))
            {
                _client.DeviceId = AppConfig.HyperionDeviceId;
            }
            if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceFp))
            {
                _client.DeviceFp = AppConfig.HyperionDeviceFp;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare device fingerprint");
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
        await RefreshDerivedTokensAsync(tokenSet, cancellationToken);
        await SyncToToolboxAsync(tokenSet, cancellationToken);
        _logger.LogInformation("Passport account logged in ({method}, aid: {aid}).", tokenSet.LoginMethod, tokenSet.Aid);
        var outcome = PassportLoginOutcome.Succeeded();
        outcome.Aid = tokenSet.Aid;
        outcome.Mid = tokenSet.Mid;
        outcome.AccountName = tokenSet.AccountName;
        return outcome;
    }




    #endregion




    #region 派生令牌与自动刷新




    /// <summary>
    /// 获取（刷新）LToken 与 CookieToken。这两个令牌会随时间失效，但只要 SToken 有效就可以重新换取。
    /// </summary>
    private async Task<bool> RefreshDerivedTokensAsync(PassportTokenSet tokenSet, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenSet.Stoken))
        {
            return false;
        }
        try
        {
            var ltokenResult = await _client.GetLTokenBySTokenAsync(tokenSet.Stoken!, tokenSet.Mid, tokenSet.Aid, cancellationToken);
            if (ltokenResult.IsSuccess)
            {
                var info = ltokenResult.Deserialize<PassportTokenInfo>();
                string? ltoken = info?.Ltoken ?? info?.Token;
                if (!string.IsNullOrWhiteSpace(ltoken))
                {
                    tokenSet.Ltoken = ltoken;
                    tokenSet.LtokenUid = ParseUid(info?.Uid) is long uid && uid > 0 ? uid : tokenSet.Aid;
                }
            }
            else
            {
                _logger.LogWarning("Get ltoken failed: {retcode} {message}", ltokenResult.Retcode, ltokenResult.Message);
            }
            var cookieResult = await _client.GetCookieTokenBySTokenAsync(tokenSet.Stoken!, tokenSet.Mid, tokenSet.Aid, cancellationToken);
            if (cookieResult.IsSuccess)
            {
                var info = cookieResult.Deserialize<PassportTokenInfo>();
                if (!string.IsNullOrWhiteSpace(info?.CookieToken))
                {
                    tokenSet.CookieToken = info.CookieToken;
                }
                if (info?.AccountId > 0)
                {
                    tokenSet.LtokenUid = info.AccountId;
                }
            }
            else
            {
                _logger.LogWarning("Get cookie token failed: {retcode} {message}", cookieResult.Retcode, cookieResult.Message);
            }
            bool success = tokenSet.HasDerivedToken;
            if (success)
            {
                tokenSet.DerivedTokenTime = DateTimeOffset.Now;
                SaveTokenSetOrRecord(tokenSet);
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh derived tokens");
            return false;
        }
    }




    private static long? ParseUid(string? uid)
    {
        return long.TryParse(uid, out long value) ? value : null;
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
            if (!await RefreshDerivedTokensAsync(tokenSet, cancellationToken))
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
    /// 返回（已刷新的账号数，登录已失效的账号数）。
    /// </para>
    /// </summary>
    public async Task<(int Refreshed, int Expired)> RefreshAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        int refreshed = 0;
        int expired = 0;
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
                    expired++;
                }
            }
            _accountItems = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh all passport accounts");
        }
        return (refreshed, expired);
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
            if (_gameRecordService.IsHoyolab)
            {
                // 通行证仅支持国服，国际服工具箱不受影响。
                return;
            }
            var user = _gameRecordService.GetRecordUser(tokenSet.Aid, false);
            if (user is null)
            {
                user = await _gameRecordService.AddRecordUserAsync(cookie, cancellationToken);
                await _gameRecordService.AddGameRolesAsync(cookie, cancellationToken);
                WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
                _logger.LogInformation("Passport account synced to miyoushe toolbox (new user).");
            }
            else if (!string.Equals(user.Cookie, cookie, StringComparison.Ordinal))
            {
                // Cookie 变化时只更新数据库，不额外请求接口。
                _gameRecordService.UpdateRecordUserCookie(tokenSet.Aid, false, cookie);
                _logger.LogInformation("Passport account cookie updated in miyoushe toolbox.");
            }
            // 米游社昵称随工具箱账号信息一起获得，用于界面展示，不额外请求接口。
            if (!string.IsNullOrWhiteSpace(user.Nickname) && tokenSet.Nickname != user.Nickname)
            {
                tokenSet.Nickname = user.Nickname;
                SaveTokenSet(tokenSet);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync passport account to miyoushe toolbox");
        }
    }




    #endregion




    #region 多账号记录与切换




    /// <summary>账号列表的缓存，任何写操作后都会失效。</summary>
    private IReadOnlyList<PassportAccountItem>? _accountItems;




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
                list.Add(new PassportAccountItem
                {
                    Key = record.Key,
                    DisplayName = tokenSet.DisplayName ?? Lang.PassportLogin_UnknownAccount,
                    Detail = GetAccountIdText(tokenSet),
                    AvatarUrl = GetAccountAvatarUrl(tokenSet.Aid) ?? PassportAccountItem.DefaultAvatarUrl,
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
            var roles = _gameRecordService.GetGameRoles(gameBiz);
            var current = TokenSet;
            string? currentKey = current?.HasStoken is true ? PassportAccountRecord.GetKey(current) : null;
            foreach (var record in QueryOrderedAccountRecords())
            {
                var tokenSet = DecryptTokenSet(record);
                if (tokenSet?.HasStoken is not true)
                {
                    continue;
                }
                var role = roles.FirstOrDefault(x => x.Uid > 0 && GameRecordService.GetCookieAccountId(x.Cookie) == tokenSet.Aid);
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
            using (var dapper = DatabaseService.CreateConnection())
            {
                if (dapper.Execute("DELETE FROM PassportAccount WHERE Key = @key;", new { key }) == 0)
                {
                    return false;
                }
            }
            _accountItems = null;
            var current = TokenSet;
            if (current?.HasStoken is true && PassportAccountRecord.GetKey(current) == key)
            {
                var next = QueryOrderedAccountRecords().Select(DecryptTokenSet).FirstOrDefault(x => x?.HasStoken is true);
                SaveTokenSet(next);
                AppConfig.SetCurrentPassportAccountKey(AppConfig.CurrentGameBiz, next is null ? null : PassportAccountRecord.GetKey(next));
                WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
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
            WeakReferenceMessenger.Default.Send(new GameRecordRoleChangedMessage(null));
            _logger.LogInformation("Switched passport account (biz: {biz}).", gameBiz);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Use passport account ({key})", key);
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
    /// 找回某个通行证账号在当前游戏里对应的角色。通行证 ID 和游戏内 UID 不是同一套编号，
    /// 唯一的对应关系来自米游社工具箱里的角色数据。
    /// <para>
    /// 角色的 Cookie 会随令牌刷新而变化，但其中的 account_id 始终是这个通行证账号，
    /// 所以按它来对应，而不是整段 Cookie。
    /// </para>
    /// </summary>
    private GameRecordRole? GetGameRoleOfAccount(PassportTokenSet tokenSet, GameBiz? gameBiz = null)
    {
        try
        {
            var biz = gameBiz ?? AppConfig.CurrentGameBiz;
            if (string.IsNullOrWhiteSpace(biz.Value) || tokenSet.Aid <= 0)
            {
                return null;
            }
            return _gameRecordService.GetGameRoles(biz)
                .FirstOrDefault(x => x.Uid > 0 && GameRecordService.GetCookieAccountId(x.Cookie) == tokenSet.Aid);
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
        return _gameRecordService.GetRecordUser(aid, false)?.AvatarUrl
            ?? _gameRecordService.GetRecordUser(aid, true)?.AvatarUrl;
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
                string? nickname = _gameRecordService.GetRecordUser(tokenSet.Aid, false)?.Nickname;
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
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<PassportAccountRecord>("SELECT * FROM PassportAccount ORDER BY Time DESC;").ToList();
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
            // 顺序变了，所有展示账号列表的地方都得刷新（主界面账号入口上叠的那几个头像就是按顺序取的，
            // 不发这条消息它会一直显示旧的头几个账号）。
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
        string json = System.Text.Json.JsonSerializer.Serialize(tokenSet, PassportJson.Options);
        string? protectedJson = DpapiHelper.Protect(json);
        if (string.IsNullOrWhiteSpace(protectedJson))
        {
            return;
        }
        var record = new PassportAccountRecord
        {
            Key = PassportAccountRecord.GetKey(tokenSet),
            Aid = tokenSet.Aid,
            Mid = tokenSet.Mid,
            Nickname = tokenSet.Nickname,
            AccountName = tokenSet.AccountName,
            LoginMethod = tokenSet.LoginMethod,
            LoginTime = tokenSet.LoginTime,
            Token = Encoding.UTF8.GetBytes(protectedJson),
            Time = DateTimeOffset.Now,
        };
        using var dapper = DatabaseService.CreateConnection();
        // 新登录的账号排到已有账号后面：先看这条 key 是不是新的，是新的就追加到当前顺序末尾。
        bool isNewAccount = dapper.ExecuteScalar<long>("SELECT COUNT(1) FROM PassportAccount WHERE Key = @Key;", new { record.Key }) == 0;
        dapper.Execute("""
            INSERT OR REPLACE INTO PassportAccount (Key, Aid, Mid, Nickname, AccountName, LoginMethod, LoginTime, Token, Time)
            VALUES (@Key, @Aid, @Mid, @Nickname, @AccountName, @LoginMethod, @LoginTime, @Token, @Time);
            """, record);
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
            return string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<PassportTokenSet>(json, PassportJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decrypt passport account token");
            return null;
        }
    }




    #endregion


}
