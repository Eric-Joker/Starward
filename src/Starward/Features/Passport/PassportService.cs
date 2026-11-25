using CommunityToolkit.HighPerformance;
using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Starward.Core;
using Starward.Core.DeviceFp;
using Starward.Core.GameRecord;
using Starward.Core.HoYoPlay;
using Starward.Core.Passport;
using Starward.Features.Account;
using Starward.Features.Database;
using Starward.Features.GameSelector;
using Starward.GameInstall;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.Passport;

internal class PassportService
{

    private readonly ILogger<PassportService> _logger;

    /// <summary>读账号表的那几个方法都是静态的，这里给它们一个惰性日志实例。</summary>
    private static readonly Lazy<ILogger<PassportService>> _sharedLogger = new(AppConfig.GetLogger<PassportService>);

    private readonly PassportClient _passportClient;

    private readonly HoYoPlayService _hoYoPlayService;

    private static byte[] MachineSecretKey => field ??= GenerateSecretKey();



    public PassportService(ILogger<PassportService> logger, PassportClient passportClient, HoYoPlayService hoYoPlayService)
    {
        _logger = logger;
        _passportClient = passportClient;
        _hoYoPlayService = hoYoPlayService;
    }



    public async Task VerifySessionAsync(PassportUserInfoModel model, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(model.Stoken))
        {
            model.ErrorType = PassportErrorType.NeedLogin;
            return;
        }
        try
        {
            var result = await _passportClient.VerifySessionAsync(model, cancellation);
            await UpdateBbsUserInfoAsync(model, cancellation);
            if (!string.IsNullOrWhiteSpace(result.NewSToken))
            {
                // refresh = true 时服务端会换发一枚新的 SToken，旧的那枚随时可能作废。
                // 账号池（<see cref="PassportAccountService"/>）里存的是另一份副本，不同步过去的话
                // 它下次写入账号表就会把旧令牌盖回来，账号中心随即又显示「登录已过期」。
                model.Stoken = result.NewSToken;
                SavePassportUserInfo(model);
                WeakReferenceMessenger.Default.Send(new PassportStokenRotatedMessage(model.Region, model.Aid, model.Mid, model.Stoken));
            }
            else
            {
                UpdatePassportUserInfo(model);
            }
            model.ErrorType = PassportErrorType.Success;
        }
        catch (miHoYoApiException ex) when (IsSessionExpired(ex.ReturnCode))
        {
            _logger.LogWarning("Passport session of account {aid} is no longer valid: {retcode} {message}", model.Aid, ex.ReturnCode, ex.Message);
            model.ErrorType = PassportErrorType.NeedLogin;
        }
        catch (Exception ex)
        {
            // 网络抖动、风控（10035 / 10041 之类）、服务端临时故障都不代表登录失效，
            // 之前这里一律按「需要重新登录」处理，账号中心因此会显示假的红色警告点。
            // 这类失败只记日志，保持上一次的结论。
            _logger.LogWarning(ex, "Verify the passport session of account {aid}", model.Aid);
        }
    }


    /// <summary>
    /// 返回码是否表示会话本身已经失效（需要用户重新登录）。
    /// 其余错误（风控、限流、网络）只是这一轮请求失败，不能据此判定账号过期。
    /// </summary>
    private static bool IsSessionExpired(int retcode)
    {
        return retcode is -100 or -111 or 10001 or -10001;
    }


    public async Task UpdateBbsUserInfoAsync(PassportUserInfoModel info, CancellationToken cancellation = default)
    {
        try
        {
            var user = await _passportClient.GetBbsUserInfoAsync(info.Region, info.Aid, cancellation);
            if (!string.IsNullOrWhiteSpace(user.Nickname))
            {
                info.Nickname = user.Nickname;
            }
            if (!string.IsNullOrWhiteSpace(user.AvatarUrl))
            {
                info.AvatarUrl = user.AvatarUrl;
            }
            info.Pendant = user.Pendant;
        }
        catch { }
    }


    public async Task<string?> CreateAuthTicketAsync(GameId gameId, CancellationToken cancellation = default)
    {
        LauncherConfigModel? launcher = LauncherConfigModel.FromId(_hoYoPlayService.GetLauncherConfig(gameId)?.Id);
        if (launcher is null || !launcher.CanLogin)
        {
            return null;
        }
        long aid = AppConfig.GetLastPassportUserAid(launcher.Region);
        using var dapper = DatabaseService.CreateConnection();
        var info = dapper.QueryFirstOrDefault<EncryptedPassportUserInfo>("""
            SELECT * FROM PassportUserInfo WHERE Region = @Region AND Aid = @aid;
            """, new { launcher.Region, aid });
        if (info is null)
        {
            return null;
        }
        var model = info.ToModel();
        model.Stoken = DecryptStoken(info.Stoken);
        var result = await _passportClient.CreateAuthTicketAsync(model, gameId.Id, cancellation);
        return result.Ticket;
    }


    public async Task LogoutAsync(PassportUserInfoModel model, CancellationToken cancellation = default)
    {
        try
        {
            await _passportClient.LogoutAsync(model, cancellation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout passport");
        }
    }


    public async Task<string> GetWebCookiesAsync(PassportUserInfo info, AndroidDeviceInfo? deviceInfo, CancellationToken cancellation = default)
    {
        string ltokenValue;
        string cookieTokenValue;
        if (info.IsGlobalServer)
        {
            var result = await _passportClient.GetBySTokenAsync(info, [TokenType.LToken, TokenType.CookieToken], deviceInfo, cancellation);
            ltokenValue = result.GetToken(TokenType.LToken) ?? throw new miHoYoApiException(-1, "Can not get ltoken.");
            cookieTokenValue = result.GetToken(TokenType.CookieToken) ?? throw new miHoYoApiException(-1, "Can not get cookie_token.");
        }
        else
        {
            var ltoken = await _passportClient.ExchangeTokenAsync(info, TokenType.LToken, deviceInfo, cancellation);
            var cookieToken = await _passportClient.ExchangeTokenAsync(info, TokenType.CookieToken, deviceInfo, cancellation);
            ltokenValue = ltoken.Token?.Token ?? throw new miHoYoApiException(-1, "Can not get ltoken.");
            cookieTokenValue = cookieToken.Token?.Token ?? throw new miHoYoApiException(-1, "Can not get cookie_token.");
        }
        return $"ltoken={ltokenValue};ltuid={info.Aid};account_id={info.Aid};cookie_token={cookieTokenValue};account_mid_v2={info.Mid};account_id_v2={info.Aid};cookie_token_v2={cookieTokenValue};ltoken_v2={ltokenValue};ltmid_v2={info.Mid};ltuid_v2={info.Aid}";
    }


    public async Task<List<GameRecordRole>> UpdateGameRolesAsync(PassportUserInfo info, AndroidDeviceInfo? deviceInfo, CancellationToken cancellation = default)
    {
        var roles = await _passportClient.GetUserGameRolesAsync(info, deviceInfo, cancellation);
        string cookie = await GetWebCookiesAsync(info, deviceInfo, cancellation);
        foreach (var role in roles)
        {
            role.Cookie = cookie;
            role.Aid = info.Aid;
        }
        return roles;
    }


    public static List<PassportUserInfoModel> GetAllPassportUserInfos()
    {
        using var dapper = DatabaseService.CreateConnection();
        var infos = dapper.Query<EncryptedPassportUserInfo>("SELECT * FROM PassportUserInfo;");
        var list = new List<PassportUserInfoModel>();
        foreach (EncryptedPassportUserInfo item in infos)
        {
            var model = item.ToModel();
            list.Add(model);
            if (item.Stoken is null || item.Stoken.Length <= 28)
            {
                MarkUnreadableStoken(model, item.Aid, null);
                continue;
            }
            try
            {
                model.Stoken = DecryptStoken(item.Stoken);
            }
            catch (Exception ex)
            {
                MarkUnreadableStoken(model, item.Aid, ex);
            }
        }
        return list;
    }


    public static List<PassportUserInfoModel> GetPassportUserInfos(ServerRegion region)
    {
        using var dapper = DatabaseService.CreateConnection();
        var infos = dapper.Query<EncryptedPassportUserInfo>("SELECT * FROM PassportUserInfo WHERE Region = @region;", new { region });
        var list = new List<PassportUserInfoModel>();
        foreach (EncryptedPassportUserInfo item in infos)
        {
            var model = item.ToModel();
            list.Add(model);
            if (item.Stoken is null || item.Stoken.Length <= 28)
            {
                MarkUnreadableStoken(model, item.Aid, null);
                continue;
            }
            try
            {
                model.Stoken = DecryptStoken(item.Stoken);
            }
            catch (Exception ex)
            {
                MarkUnreadableStoken(model, item.Aid, ex);
            }
        }
        return list;
    }



    public static PassportUserInfoModel? GetPassportUserInfo(long aid)
    {
        using var dapper = DatabaseService.CreateConnection();
        var info = dapper.QueryFirstOrDefault<EncryptedPassportUserInfo>("SELECT * FROM PassportUserInfo WHERE Aid = @aid LIMIT 1;", new { aid });
        if (info is null)
        {
            return null;
        }
        var model = info.ToModel();
        if (info.Stoken is null || info.Stoken.Length <= 28)
        {
            MarkUnreadableStoken(model, info.Aid, null);
            return model;
        }
        try
        {
            model.Stoken = DecryptStoken(info.Stoken);
        }
        catch (Exception ex)
        {
            MarkUnreadableStoken(model, info.Aid, ex);
        }
        return model;
    }


    /// <summary>
    /// 账号表里的 SToken 读不出来：标注为「需要重新登录」，并留下日志。
    /// <para>
    /// 这是账号中心红色警告点的另一个来源（另一个是 <see cref="VerifySessionAsync"/> 校验失败）。
    /// 以前这里静默处理，界面上只看到一个红点，看不出是解密失败还是令牌真的失效。
    /// </para>
    /// </summary>
    private static void MarkUnreadableStoken(PassportUserInfoModel model, long aid, Exception? ex)
    {
        model.ErrorType = PassportErrorType.NeedLogin;
        try
        {
            if (ex is null)
            {
                _sharedLogger.Value.LogWarning("The stored stoken of account {aid} is empty or truncated.", aid);
            }
            else
            {
                _sharedLogger.Value.LogWarning(ex, "Can not decrypt the stored stoken of account {aid}.", aid);
            }
        }
        catch { }
    }



    public static void SavePassportUserInfo(PassportUserInfoModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Stoken))
        {
            return;
        }
        var encryptedInfo = EncryptedPassportUserInfo.FromModel(model);
        encryptedInfo.Stoken = EncryptStoken(model.Stoken);
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO PassportUserInfo (Region, Aid, Mid, Stoken, SecretType, Nickname, AvatarUrl, Pendant)
            VALUES (@Region, @Aid, @Mid, @Stoken, @SecretType, @Nickname, @AvatarUrl, @Pendant);
            """, encryptedInfo);
    }


    public static void UpdatePassportUserInfo(PassportUserInfoModel model)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            UPDATE PassportUserInfo SET Nickname = @Nickname, AvatarUrl = @AvatarUrl, Pendant = @Pendant WHERE Region = @Region AND Aid = @Aid;
            """, model);
    }


    public static void DeletePassportUserInfo(PassportUserInfoModel model)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("DELETE FROM PassportUserInfo WHERE Region = @Region AND Aid = @Aid;", model);
    }


    private static byte[] GenerateSecretKey()
    {
        string guid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string ?? Environment.MachineName;
        Span<byte> salt = SHA256.HashData(Encoding.UTF8.GetBytes(guid)).AsSpan()[..12];
        return Rfc2898DeriveBytes.Pbkdf2(guid, salt, 1000, HashAlgorithmName.SHA256, 32);
    }


    private static byte[] EncryptStoken(string stoken)
    {
        ReadOnlySpan<byte> stokenBytes = Encoding.UTF8.GetBytes(stoken);
        byte[] cipher = new byte[stokenBytes.Length + 28];
        RandomNumberGenerator.Fill(cipher.AsSpan()[..12]);
        using var stokenAes = new AesGcm(MachineSecretKey, 16);
        stokenAes.Encrypt(cipher.AsSpan()[..12], stokenBytes, cipher.AsSpan()[12..^16], cipher.AsSpan()[^16..]);
        return cipher;
    }


    private static string DecryptStoken(byte[] cipher)
    {
        byte[] stoken = new byte[cipher.Length - 28];
        using var stokenAes = new AesGcm(MachineSecretKey, 16);
        stokenAes.Decrypt(cipher.AsSpan()[..12], cipher.AsSpan()[12..^16], cipher.AsSpan()[^16..], stoken);
        return Encoding.UTF8.GetString(stoken);
    }


    private class EncryptedPassportUserInfo
    {
        public ServerRegion Region { get; set; }

        public long Aid { get; set; }

        public string Mid { get; set; }

        public byte[] Stoken { get; set; }

        public int SecretType { get; set; }

        public string Nickname { get; set; }

        public string AvatarUrl { get; set; }

        public string? Pendant { get; set; }


        public static EncryptedPassportUserInfo FromModel(PassportUserInfoModel info)
        {
            return new EncryptedPassportUserInfo()
            {
                Region = info.Region,
                Aid = info.Aid,
                Mid = info.Mid,
                Nickname = info.Nickname,
                AvatarUrl = info.AvatarUrl,
                Pendant = info.Pendant,
            };
        }


        public PassportUserInfoModel ToModel()
        {
            return new PassportUserInfoModel
            {
                Region = Region,
                Aid = Aid,
                Mid = Mid,
                Nickname = Nickname,
                AvatarUrl = AvatarUrl,
                Pendant = Pendant,
            };
        }


    }

}
