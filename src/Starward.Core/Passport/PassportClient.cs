using Starward.Core.DeviceFp;
using Starward.Core.GameRecord;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Starward.Core.Passport;


/// <summary>
/// 米哈游通行证客户端。
/// <para>
/// 上游（0.19）的强类型接口是骨架：会话校验、登出、跨端免密登录、游戏授权票据、V2 换票、
/// stoken 换 token、账号下游戏角色，统一通过 <see cref="SendAsync{T}(HttpRequestMessage, CancellationToken)"/>
/// 解析 <see cref="miHoYoApiWrapper{T}"/> 返回结构。
/// </para>
/// <para>
/// 国服登录协议（原 Moonward 分支）并入同一个客户端：密码、短信验证码、新设备校验、扫码登录、
/// 游戏内二维码、票据换票等接口通过 <see cref="SendAsync(HttpRequestMessage, CancellationToken)"/>
/// 返回原始 <see cref="PassportResult"/>，因为需要读取 <c>x-rpc-aigis</c>、<c>x-rpc-verify</c>
/// 响应头与 Set-Cookie，并且失败时要把返回码交给调用方决定后续流程（人机验证 / 新设备校验）。
/// </para>
/// </summary>
public class PassportClient
{


    #region Constant


    public const string HostPassport = "https://passport-api.mihoyo.com";

    public const string HostTakumi = "https://api-takumi.mihoyo.com";

    public const string HostGameSdk = "https://hk4e-sdk.mihoyo.com";


    /// <summary>米游社（国服）应用 ID。</summary>
    public const string AppIdBbs = "bll8iq97cem8";

    /// <summary>米哈游启动器应用 ID，用于通行证会话与授权票据。</summary>
    public const string AppIdHyp = "ddxf5dufpuyo";


    /// <summary>米游社版本号。</summary>
    public const string BbsVersion = "2.119.0";


    /// <summary>DS2 签名使用的 salt（与客户端类型 5 / 4 通用，不随版本变化）。</summary>
    private const string ApiSalt2 = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";


    /// <summary>
    /// 通行证 SDK 版本，V2 换票接口的 <c>x-rpc-sdk_version</c> / <c>x-rpc-account_version</c> 请求头使用。
    /// </summary>
    public const string PassportSdkVersion = "2.44.0";


    /// <summary>
    /// 通行证 SDK 的 DS 签名 salt，与米游社 Android 2.119.0 内置的 passport SDK 一致。
    /// </summary>
    private const string PassportDsSalt = "JwYDpKvLj6MrMqqYU6jTKF17KNO2PXoS";


    /// <summary>
    /// 模拟通行证 SDK 的生命周期标识，进程启动时生成一次，所有换票请求共用。
    /// </summary>
    private static readonly string PassportLifecycleId = Guid.NewGuid().ToString("D");


    /// <summary>米哈游通行证 RSA 公钥，用于加密账号、密码与手机号。</summary>
    private const string PassportPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDDvekdPMHN3AYhm/vktJT+YJr7
        cI5DcsNKqdsx5DZX0gDuWFuIjzdwButrIYPNmRJ1G8ybDIF7oDW2eEpm5sMbL9zs
        9ExXCdvqrn51qELbqj0XxtMTIpaCHFSI50PfPpTFV9Xt/hmyVwokoOXFlAEgCn+Q
        CgGs52bFoYMtyi+xEQIDAQAB
        -----END PUBLIC KEY-----
        """;


    public const string UserAgent = $"Mozilla/5.0 (Linux; Android 13; Pixel 5 Build/TQ3A.230901.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/118.0.0.0 Mobile Safari/537.36 miHoYoBBS/{BbsVersion}";


    #endregion




    private readonly HttpClient _httpClient;


    public PassportClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
    }




    private string _language = "";


    /// <summary>
    /// 请求语言。默认空串，与上游一致（不主动指定语言，由服务端决定）；
    /// 调用方赋值时会经过 <see cref="LanguageUtil.FilterLanguage"/> 归一化。
    /// </summary>
    public string Language
    {
        get => _language;
        set => _language = LanguageUtil.FilterLanguage(value);
    }


    /// <summary>设备 ID，与米游社工具箱共用。</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");


    /// <summary>设备指纹，与米游社工具箱共用。</summary>
    public string DeviceFp { get; set; } = "0000000000000";


    /// <summary>设备名，V2 换票时写入 <c>x-rpc-device_name</c>。</summary>
    public string DeviceName { get; set; } = "Moonward";


    /// <summary>设备型号，V2 换票时写入 <c>x-rpc-device_model</c>。</summary>
    public string DeviceModel { get; set; } = "PC";




    #region 请求基础




    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };


    /// <summary>
    /// 发送请求并解析成 <see cref="PassportResult"/>。
    /// <para>
    /// 国服登录协议的所有接口都使用同一套返回结构，且人机验证（-3101）、新设备校验（-3235）
    /// 需要读取响应头，因此这里不抛异常，由调用方决定如何处理；
    /// 需要强类型返回且失败即抛 <see cref="miHoYoApiException"/> 的上游接口请使用 <see cref="SendAsync{T}(HttpRequestMessage, CancellationToken)"/>。
    /// </para>
    /// </summary>
    private async Task<PassportResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);

        var result = new PassportResult();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("retcode", out var retcode))
            {
                result.Retcode = retcode.ValueKind switch
                {
                    JsonValueKind.Number => retcode.GetInt32(),
                    JsonValueKind.String => int.TryParse(retcode.GetString(), out int code) ? code : -1,
                    _ => -1,
                };
            }
            else
            {
                result.Retcode = (int)response.StatusCode == 200 ? 0 : -(int)response.StatusCode;
            }
            if (root.TryGetProperty("message", out var message) && message.ValueKind is JsonValueKind.String)
            {
                result.Message = message.GetString();
            }
            else if (root.TryGetProperty("msg", out var msg) && msg.ValueKind is JsonValueKind.String)
            {
                result.Message = msg.GetString();
            }
            if (root.TryGetProperty("data", out var data))
            {
                result.Data = data.Clone();
            }
        }
        catch (JsonException)
        {
            result.Retcode = -1;
            result.Message = text;
        }

        if (response.Headers.TryGetValues("x-rpc-verify", out var verify))
        {
            result.VerifyHeader = verify.FirstOrDefault();
        }
        if (response.Headers.TryGetValues("x-rpc-aigis", out var aigis))
        {
            result.AigisHeader = aigis.FirstOrDefault();
        }
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (string header in setCookies)
            {
                int index = header.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }
                string name = header[..index].Trim();
                string value = header[(index + 1)..];
                int end = value.IndexOf(';');
                if (end >= 0)
                {
                    value = value[..end];
                }
                result.Cookies[name] = value.Trim();
            }
        }
        return result;
    }




    /// <summary>
    /// 上游强类型接口的发送通道：解析 <see cref="miHoYoApiWrapper{T}"/>，失败时抛 <see cref="miHoYoApiException"/>。
    /// </summary>
    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken) where T : class
    {
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var wrapper = await response.Content.ReadFromJsonAsync(typeof(miHoYoApiWrapper<T>), PassportJsonContext.Default, cancellationToken).ConfigureAwait(false) as miHoYoApiWrapper<T>;
        if (wrapper is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.");
        }
        if (wrapper.Retcode != 0)
        {
            throw new miHoYoApiException(wrapper.Retcode, wrapper.Message);
        }
        return wrapper.Data;
    }




    private static JsonContent CreateJsonContent(JsonObject body)
    {
        return JsonContent.Create(body, options: JsonOptions);
    }




    /// <summary>
    /// 米游社应用通用请求头。通行证国服接口只需要 <c>x-rpc-*</c> 系列请求头，不需要 DS 签名。
    /// </summary>
    /// <param name="clientType">客户端类型，2 安卓、3 其它、5 网页。</param>
    /// <param name="gameBiz">游戏业务标识，默认米游社。</param>
    /// <param name="referer">来源页面。</param>
    private void AddBbsAppHeaders(HttpRequestMessage request, string clientType = "2", string gameBiz = "bbs_cn", string referer = "https://app.mihoyo.com")
    {
        request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppIdBbs);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", clientType);
        request.Headers.TryAddWithoutValidation("x-rpc-game_biz", gameBiz);
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        request.Headers.TryAddWithoutValidation("x-rpc-device_fp", DeviceFp);
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", BbsVersion);
        request.Headers.TryAddWithoutValidation("Referer", referer);
    }




    /// <summary>通行证会话（启动器）通用请求头。</summary>
    private void AddHypHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppIdHyp);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "3");
        request.Headers.TryAddWithoutValidation("x-rpc-game_biz", "hyp_cn");
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        request.Headers.TryAddWithoutValidation("x-rpc-device_fp", DeviceFp);
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com");
    }




    /// <summary>
    /// 上游强类型接口的通行证请求头：按账号区域选择国服 / 国际服的应用标识与 SDK 版本。
    /// </summary>
    private void AddPassportHeaders(HttpRequestMessage request, PassportUserInfo userInfo)
    {
        request.Headers.Add("x-rpc-app_id", userInfo.IsGlobalServer ? "ddxf6vlr1reo" : "ddxf5qt290cg");
        request.Headers.Add("x-rpc-client_type", "3");
        request.Headers.Add("x-rpc-game_biz", userInfo.IsGlobalServer ? "hyp_global" : "hyp_cn");
        request.Headers.Add("x-rpc-sdk_version", userInfo.IsGlobalServer ? "2.55.3" : "2.55.2");
        request.Headers.Add("x-rpc-device_id", DeviceId);
        request.Headers.Add("x-rpc-device_fp", DeviceFp);
        request.Headers.Add("x-rpc-device_model", "x86_64");
        request.Headers.Add("x-rpc-device_name", Environment.MachineName);
        request.Headers.Add("x-rpc-device_os", Environment.OSVersion.Version.Build > 22000 ? "Windows 11 Version 2009" : "Windows 10 Version 1909");
        request.Headers.Add("x-rpc-language", Language);
    }




    /// <summary>
    /// 生成 DS2 签名。只有少量接口（例如扫码登录）需要，
    /// 签名算法与米游社工具箱一致：<c>md5(salt={salt}&amp;t={t}&amp;r={r}&amp;b={body}&amp;q={query})</c>。
    /// </summary>
    private static string CreateSecret2(string url, string? body)
    {
        int t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = Random.Shared.Next(100000, 200000).ToString();
        string q = "";
        int index = url.IndexOf('?');
        if (index >= 0)
        {
            q = string.Join("&", url[(index + 1)..].Split('&').OrderBy(x => x));
        }
        string b = body ?? "";
        byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes($"salt={ApiSalt2}&t={t}&r={r}&b={b}&q={q}"));
        return $"{t},{r},{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }




    /// <summary>
    /// 生成通行证 SDK 的 DS 签名（<c>md5(salt={salt}&amp;t={t}&amp;r={r}&amp;b={body}&amp;q=)</c>）。
    /// 换票是 POST 且没有 query，<c>q</c> 固定为空。
    /// </summary>
    /// <param name="body">与请求体逐字节一致的 JSON 原文。</param>
    private static string CreatePassportSecret(string body)
    {
        int t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = CreateRandomNonce();
        byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes($"salt={PassportDsSalt}&t={t}&r={r}&b={body}&q="));
        return $"{t},{r},{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }




    /// <summary>生成 DS 签名使用的 6 位小写字母数字随机串。</summary>
    private static string CreateRandomNonce()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(6, chars, static (span, alphabet) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[Random.Shared.Next(alphabet.Length)];
            }
        });
    }




    /// <summary>
    /// 写入风控校验请求头 <c>x-rpc-aigis</c>。触发人机验证后，需要用同一个请求重试并带上该请求头。
    /// </summary>
    private static void ApplyCaptcha(HttpRequestMessage request, PassportCaptcha? captcha)
    {
        if (captcha is null || captcha.IsEmpty || string.IsNullOrWhiteSpace(captcha.SessionId))
        {
            return;
        }
        request.Headers.TryAddWithoutValidation("x-rpc-aigis", captcha.ToAigisHeaderValue());
    }




    #endregion




    #region 人机验证 / 新设备校验




    /// <summary>
    /// 解析 <c>-3101</c> 响应的 <c>x-rpc-aigis</c> 头。注意其中的 <c>data</c> 可能是对象也可能是 JSON 字符串。
    /// </summary>
    public static PassportAigisHeader? ParseAigisHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<PassportAigisHeader>(header, PassportJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }




    /// <summary>
    /// 解析 <c>-3235</c> 响应的 <c>x-rpc-verify</c> 头。
    /// </summary>
    public static PassportVerifyHeader? ParseVerifyHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<PassportVerifyHeader>(header, PassportJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }




    #endregion




    #region 账号密码




    /// <summary>
    /// 使用通行证 RSA 公钥加密字符串（PKCS#1 v1.5 + Base64）。
    /// 账号、密码、手机号、区号都需要加密后传输。
    /// </summary>
    public static string EncryptByPublicKey(string text)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PassportPublicKey);
        byte[] encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(text), RSAEncryptionPadding.Pkcs1);
        return Convert.ToBase64String(encrypted);
    }




    /// <summary>
    /// 账号密码登录。账号与密码都会用通行证公钥加密。
    /// </summary>
    /// <param name="account">手机号或邮箱。</param>
    /// <param name="password">密码。</param>
    /// <param name="captcha">人机验证结果，未触发时传 null。</param>
    public async Task<PassportResult> LoginByPasswordAsync(string account, string password, PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["account"] = EncryptByPublicKey(account),
            ["password"] = EncryptByPublicKey(password),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-passport/app/loginByPassword")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request);
        ApplyCaptcha(request, captcha);
        return await SendAsync(request, cancellationToken);
    }




    #endregion




    #region 短信验证码




    /// <summary>
    /// 发送登录短信验证码。返回的 <see cref="PassportMobileCaptchaData.ActionType"/> 需要原样回传给登录接口。
    /// </summary>
    /// <param name="mobile">手机号。</param>
    /// <param name="areaCode">国家区号，例如 <c>+86</c>。</param>
    /// <param name="captcha">人机验证结果，未触发时传 null。</param>
    public async Task<PassportResult> CreateLoginCaptchaAsync(string mobile, string areaCode = "+86", PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["area_code"] = EncryptByPublicKey(areaCode),
            ["mobile"] = EncryptByPublicKey(mobile),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-verifier/verifier/createLoginCaptcha")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request, referer: "https://user.miyoushe.com/", gameBiz: "hk4e_cn");
        ApplyCaptcha(request, captcha);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 使用短信验证码登录。<paramref name="captcha"/> 为短信验证码，明文传输；手机号与区号加密。
    /// </summary>
    /// <param name="mobile">手机号。</param>
    /// <param name="captcha">收到的短信验证码。</param>
    /// <param name="actionType">发送验证码时返回的 <c>action_type</c>，需原样带回。</param>
    /// <param name="areaCode">国家区号，例如 <c>+86</c>。</param>
    /// <param name="aigis">风控人机验证结果，未触发时传 null。</param>
    public async Task<PassportResult> LoginByMobileCaptchaAsync(string mobile, string captcha, string? actionType = null, string areaCode = "+86", PassportCaptcha? aigis = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["area_code"] = EncryptByPublicKey(areaCode),
            ["mobile"] = EncryptByPublicKey(mobile),
            ["captcha"] = captcha,
        };
        if (!string.IsNullOrWhiteSpace(actionType))
        {
            body["action_type"] = actionType;
        }
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-passport/app/loginByMobileCaptcha")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request);
        ApplyCaptcha(request, aigis);
        return await SendAsync(request, cancellationToken);
    }




    #endregion




    #region 新设备短信校验（retcode = -3235）




    /// <summary>
    /// 新设备登录校验第一步：向校验票据对应的手机号发送验证码。
    /// </summary>
    /// <param name="actionTicket">来自 <c>x-rpc-verify</c> 响应头的 <c>action_ticket</c>。</param>
    /// <param name="captcha">人机验证结果，未触发时传 null。</param>
    public async Task<PassportResult> CreateMobileCaptchaByActionTicketAsync(string actionTicket, PassportCaptcha? captcha = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["action_ticket"] = actionTicket,
            ["action_type"] = "verify_for_component",
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-verifier/verifier/createMobileCaptchaByActionTicket")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request);
        ApplyCaptcha(request, captcha);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 新设备登录校验第二步：提交短信验证码。<paramref name="verifyMethod"/> 1 为手机、2 为邮箱。
    /// </summary>
    /// <param name="verifyTicket">来自 <c>x-rpc-verify</c> 响应头 <c>verify_str.ticket</c> 的值。</param>
    /// <param name="mobileCaptcha">收到的短信验证码。</param>
    public async Task<PassportResult> VerifyActionTicketPartlyAsync(string verifyTicket, string mobileCaptcha, int verifyMethod = 1, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["action_ticket"] = verifyTicket,
            ["action_type"] = "verify_for_component",
            ["verify_method"] = verifyMethod,
            ["mobile_captcha"] = mobileCaptcha,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-verifier/verifier/verifyActionTicketPartly")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 新设备登录校验第三步：校验通过后获取登录信息。返回结构与登录接口一致，不需要重新提交账号密码。
    /// </summary>
    /// <param name="actionTicket">来自 <c>x-rpc-verify</c> 响应头的 <c>action_ticket</c>。</param>
    public async Task<PassportResult> CheckRiskVerifiedAsync(string actionTicket, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["action_ticket"] = actionTicket,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-passport/app/checkRiskVerified")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request);
        return await SendAsync(request, cancellationToken);
    }




    #endregion




    #region 扫码登录（米游社 App）




    /// <summary>
    /// 生成扫码登录二维码。
    /// </summary>
    public async Task<PassportResult> CreateQrLoginAsync(CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        string url = $"{HostPassport}/account/ma-cn-passport/app/createQRLogin";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request, clientType: "3");
        request.Headers.TryAddWithoutValidation("DS", CreateSecret2(url, "{}"));
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 查询扫码登录状态。确认登录后 SToken 会直接出现在返回的 <c>tokens</c> 中。
    /// </summary>
    public async Task<PassportResult> QueryQrLoginStatusAsync(string ticket, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["ticket"] = ticket,
        };
        string url = $"{HostPassport}/account/ma-cn-passport/app/queryQRLoginStatus";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request, clientType: "3");
        request.Headers.TryAddWithoutValidation("DS", CreateSecret2(url, body.ToJsonString(JsonOptions)));
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 兜底：确认扫码后用二维码票据换取登录信息（当状态查询没有返回 <c>tokens</c> 时使用）。
    /// </summary>
    public async Task<PassportResult> LoginByAuthTicketAsync(string ticket, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["ticket"] = ticket,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-passport/app/loginByAuthTicket")
        {
            Content = CreateJsonContent(body),
        };
        AddBbsAppHeaders(request);
        return await SendAsync(request, cancellationToken);
    }




    #endregion




    #region 扫码登录（游戏内二维码，换取 GameToken）




    /// <summary>
    /// 申请游戏内二维码。该流程已不推荐，仅在米游社 App 扫码流程不可用时作为备选。
    /// </summary>
    /// <param name="appId">应用标识，任何值效果相同，仅需传入。</param>
    public async Task<PassportResult> PandaQrFetchAsync(int appId = 4, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["app_id"] = appId.ToString(),
            ["device"] = DeviceId,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostGameSdk}/hk4e_cn/combo/panda/qrcode/fetch")
        {
            Content = CreateJsonContent(body),
        };
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 查询游戏内二维码状态。
    /// </summary>
    public async Task<PassportResult> PandaQrQueryAsync(string ticket, int appId = 4, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["app_id"] = appId.ToString(),
            ["device"] = DeviceId,
            ["ticket"] = ticket,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostGameSdk}/hk4e_cn/combo/panda/qrcode/query")
        {
            Content = CreateJsonContent(body),
        };
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 使用 GameToken 换取 SToken（V1）。返回的 V1 SToken 需要再转换为 V2 才能用于通行证会话。
    /// </summary>
    public async Task<PassportResult> GetTokenByGameTokenAsync(long accountId, string gameToken, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["account_id"] = accountId,
            ["game_token"] = gameToken,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostTakumi}/account/ma-cn-session/app/getTokenByGameToken")
        {
            Content = CreateJsonContent(body),
        };
        request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppIdBbs);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        request.Headers.TryAddWithoutValidation("x-rpc-device_fp", DeviceFp);
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com");
        return await SendAsync(request, cancellationToken);
    }




    #endregion




    #region 令牌转换与刷新




    /// <summary>
    /// SToken（V1）换取 SToken（V2）。V2 必须与 <c>mid</c> 搭配使用。
    /// </summary>
    /// <param name="stokenV1">以 <c>v2_</c> 开头以外的 SToken。</param>
    /// <param name="stuid">SToken（V1）需要搭配的账号 ID。</param>
    public async Task<PassportResult> GetTokenBySTokenAsync(string stokenV1, long stuid, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-session/app/getTokenBySToken");
        request.Headers.TryAddWithoutValidation("Cookie", $"stoken={stokenV1}; stuid={stuid}");
        request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppIdBbs);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "2");
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        request.Headers.TryAddWithoutValidation("x-rpc-device_fp", DeviceFp);
        request.Headers.TryAddWithoutValidation("Referer", "https://app.mihoyo.com");
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 使用 SToken 换取 LToken。LToken 会随时间失效，需要定期重新获取。
    /// <para>
    /// 旧接口 <c>getLTokenBySToken</c> 正在按账号停用（返回 1002 / -464），这里改走 V2 换票接口，
    /// 换取 <c>ltoken_v2</c>（<c>dst_token_type = 2</c>）。方法名保持不变，调用方不需要区分新旧接口。
    /// </para>
    /// </summary>
    /// <param name="stoken">SToken（V2）。</param>
    /// <param name="mid">账号的 MiHoYo ID。</param>
    public async Task<PassportResult> GetLTokenBySTokenAsync(string stoken, string mid, CancellationToken cancellationToken = default)
    {
        return await ExchangeTokenAsync(stoken, mid, 2, cancellationToken);
    }




    /// <summary>
    /// 使用 SToken 换取 CookieToken。CookieToken 同样会随时间失效。
    /// <para>
    /// 旧接口 <c>getCookieAccountInfoBySToken</c> 正在按账号停用（返回 1002 / -464），这里改走 V2 换票接口，
    /// 换取 <c>cookie_token_v2</c>（<c>dst_token_type = 4</c>）。方法名保持不变，调用方不需要区分新旧接口。
    /// </para>
    /// </summary>
    /// <param name="stoken">SToken（V2）。</param>
    /// <param name="mid">账号的 MiHoYo ID。</param>
    public async Task<PassportResult> GetCookieTokenBySTokenAsync(string stoken, string mid, CancellationToken cancellationToken = default)
    {
        return await ExchangeTokenAsync(stoken, mid, 4, cancellationToken);
    }




    /// <summary>
    /// 通行证 V2 会话换票：用 SToken 换取 <c>ltoken_v2</c>（<paramref name="dstTokenType"/> = 2）
    /// 或 <c>cookie_token_v2</c>（= 4）。
    /// <para>
    /// 请求头与 DS 签名对齐米游社 Android 2.119.0 内置的通行证 SDK：其中 <c>DS</c> 是对
    /// <b>实际发出的请求体原文</b> 计算的，所以请求体只序列化一次，签名和发送用的是同一份字符串。
    /// </para>
    /// </summary>
    /// <param name="stoken">登录得到的 SToken（V2）。</param>
    /// <param name="mid">账号的 MiHoYo ID。</param>
    /// <param name="dstTokenType">目标令牌类型：2 为 ltoken_v2，4 为 cookie_token_v2。</param>
    public async Task<PassportResult> ExchangeTokenAsync(string stoken, string mid, int dstTokenType, CancellationToken cancellationToken = default)
    {
        return await ExchangeTokenCoreAsync(stoken, mid, dstTokenType, null, cancellationToken);
    }




    /// <summary>
    /// 上行 V2 换票的统一实现：国服登录协议（无 Android 设备信息）与上游强类型接口（可带 Android 设备信息）共用，
    /// 避免同一个接口维护两份请求头/签名逻辑。
    /// </summary>
    private async Task<PassportResult> ExchangeTokenCoreAsync(string stoken, string mid, int dstTokenType, AndroidDeviceInfo? androidDevice, CancellationToken cancellationToken)
    {
        // 顺序与通行证 SDK 一致：dst_token_type、src_token{token_type, token}、mid。
        var body = new JsonObject
        {
            ["dst_token_type"] = dstTokenType,
            ["src_token"] = new JsonObject
            {
                ["token_type"] = (int)TokenType.SToken,
                ["token"] = stoken,
            },
            ["mid"] = mid,
        };
        string bodyText = body.ToJsonString(JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-session/app/exchange")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("DS", CreatePassportSecret(bodyText));
        if (androidDevice is { ExtFields: { } androidExtFields })
        {
            request.Headers.TryAddWithoutValidation("x-rpc-app_id", "ddxf5qt290cg");
            request.Headers.TryAddWithoutValidation("x-rpc-client_type", "2");
            request.Headers.TryAddWithoutValidation("x-rpc-device_id", androidExtFields.AndroidId);
            request.Headers.TryAddWithoutValidation("x-rpc-device_fp", androidDevice.DeviceFp);
            request.Headers.TryAddWithoutValidation("x-rpc-device_name", androidExtFields.DeviceName.Replace(' ', '+'));
            request.Headers.TryAddWithoutValidation("x-rpc-device_model", androidExtFields.Model);
            request.Headers.TryAddWithoutValidation("x-rpc-sys_version", androidExtFields.OsVersion);
            request.Headers.TryAddWithoutValidation("x-rpc-game_biz", "bbs_cn");
            request.Headers.TryAddWithoutValidation("x-rpc-app_version", GameRecordClientBase.HyperionAppVersion);
            request.Headers.TryAddWithoutValidation("x-rpc-sdk_version", GameRecordClientBase.HyperionSdkVersion);
            request.Headers.TryAddWithoutValidation("x-rpc-lifecycle_id", DynamicSecret.LifeCycleId);
            request.Headers.TryAddWithoutValidation("x-rpc-account_version", GameRecordClientBase.HyperionSdkVersion);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("x-rpc-app_version", BbsVersion);
            request.Headers.TryAddWithoutValidation("x-rpc-client_type", "2");
            request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppIdBbs);
            request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
            request.Headers.TryAddWithoutValidation("x-rpc-device_fp", Uri.EscapeDataString(DeviceFp));
            request.Headers.TryAddWithoutValidation("x-rpc-device_name", Uri.EscapeDataString(DeviceName));
            request.Headers.TryAddWithoutValidation("x-rpc-device_model", Uri.EscapeDataString(DeviceModel));
            request.Headers.TryAddWithoutValidation("x-rpc-game_biz", "bbs_cn");
            request.Headers.TryAddWithoutValidation("x-rpc-sys_version", "12");
            request.Headers.TryAddWithoutValidation("x-rpc-sdk_version", PassportSdkVersion);
            request.Headers.TryAddWithoutValidation("x-rpc-account_version", PassportSdkVersion);
            request.Headers.TryAddWithoutValidation("x-rpc-lifecycle_id", PassportLifecycleId);
        }
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 使用 LoginTicket 换取 LToken / SToken。
    /// </summary>
    /// <param name="loginTicket">登录票据，有效期约 30 分钟且仅能使用一次。</param>
    /// <param name="uid">米游社账号 ID。</param>
    /// <param name="tokenTypes">1 仅 SToken（已弃用）、2 仅 LToken、3 SToken 与 LToken。</param>
    public async Task<PassportResult> GetMultiTokenByLoginTicketAsync(string loginTicket, long uid, int tokenTypes = 2, CancellationToken cancellationToken = default)
    {
        string url = $"{HostTakumi}/auth/api/getMultiTokenByLoginTicket?token_types={tokenTypes}&login_ticket={Uri.EscapeDataString(loginTicket)}&uid={uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppIdBbs);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceId);
        request.Headers.TryAddWithoutValidation("x-rpc-device_fp", DeviceFp);
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com");
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 校验 SToken。SToken（V2）需要与 <c>mid</c> 一起使用；
    /// <paramref name="refresh"/> 为 true 时服务端会返回新的 SToken，这是 SToken 续期的唯一途径。
    /// </summary>
    public async Task<PassportResult> VerifyStokenAsync(string stoken, string mid, bool refresh = true, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["token"] = new JsonObject
            {
                ["token_type"] = (int)TokenType.SToken,
                ["token"] = stoken,
            },
            ["refresh"] = refresh,
            ["mid"] = mid,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-session/app/verify")
        {
            Content = CreateJsonContent(body),
        };
        AddHypHeaders(request);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 为指定游戏业务申请授权票据（用于游戏内自动登录）。
    /// </summary>
    public async Task<PassportResult> CreateAuthTicketByGameBizAsync(string gameBiz, string stoken, string mid, long aid, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["game_biz"] = gameBiz,
            ["stoken"] = stoken,
            ["uid"] = aid,
            ["mid"] = mid,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HostPassport}/account/ma-cn-verifier/app/createAuthTicketByGameBiz")
        {
            Content = CreateJsonContent(body),
        };
        AddHypHeaders(request);
        return await SendAsync(request, cancellationToken);
    }




    #endregion




    #region 通行证（上游强类型接口）




    public async Task<GameRecordUser> GetBbsUserInfoAsync(ServerRegion region, long aid, CancellationToken cancellation = default)
    {
        string url = region switch
        {
            ServerRegion.HoYoverse => $"https://bbs-api-os.hoyolab.com/community/user/wapi/getUserFullInfo?uid={aid}",
            ServerRegion.miHoYo => $"https://bbs-api.miyoushe.com/user/wapi/getUserFullInfo?uid={aid}",
            _ => throw new NotSupportedException($"Server region '{region}' is not supported.")
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://" + new Uri(url).Host);
        return (await SendAsync<GameRecordUserWrapper>(request, cancellation)).User;
    }




    /// <summary>
    /// 校验会话 stoken
    /// </summary>
    public async Task<SessionVerifyResult> VerifySessionAsync(PassportUserInfo userInfo, CancellationToken cancellationToken = default)
    {
        HttpRequestMessage request;
        if (userInfo.IsGlobalServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api-sg.hoyoverse.com/account/ma-passport/token/verifySToken")
            {
                Content = JsonContent.Create(new VerifySTokenRequest
                {
                    Mid = userInfo.Mid,
                    Stoken = userInfo.Stoken,
                }, PassportJsonContext.Default.VerifySTokenRequest),
            };
        }
        else if (userInfo.IsChinaServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api.mihoyo.com/account/ma-cn-session/app/verify")
            {
                Content = JsonContent.Create(new SessionVerifyRequest
                {
                    Token = new PassportToken { TokenType = TokenType.SToken, Token = userInfo.Stoken },
                    Refresh = true,
                    Mid = userInfo.Mid,
                }, PassportJsonContext.Default.SessionVerifyRequest),
            };
        }
        else
        {
            throw new NotSupportedException($"Passport region '{userInfo.Region}' is not supported.");
        }
        AddPassportHeaders(request, userInfo);
        return await SendAsync<SessionVerifyResult>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 登出
    /// </summary>
    public async Task LogoutAsync(PassportUserInfo userInfo, CancellationToken cancellationToken = default)
    {
        var payload = new LogoutRequest
        {
            Mid = userInfo.Mid,
            Token = new PassportToken { TokenType = TokenType.SToken, Token = userInfo.Stoken },
        };
        HttpRequestMessage request;
        if (userInfo.IsGlobalServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api-sg.hoyoverse.com/account/ma-passport/api/logout")
            {
                Content = JsonContent.Create(payload, PassportJsonContext.Default.LogoutRequest),
            };
        }
        else if (userInfo.IsChinaServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api.mihoyo.com/account/ma-cn-session/app/logout")
            {
                Content = JsonContent.Create(payload, PassportJsonContext.Default.LogoutRequest),
            };
        }
        else
        {
            throw new NotSupportedException($"Passport region '{userInfo.Region}' is not supported.");
        }
        AddPassportHeaders(request, userInfo);
        await SendAsync<LogoutResult>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 跨端免密登录
    /// </summary>
    public async Task<string> CreateCrossLoginTokenAsync(PassportUserInfo userInfo, string dest, CancellationToken cancellationToken = default)
    {
        HttpRequestMessage request;
        if (userInfo.IsGlobalServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api-sg.hoyoverse.com/account/ma-passport/token/getCrossTokenUrlBySToken")
            {
                Content = JsonContent.Create(new GetCrossTokenUrlBySTokenRequest
                {
                    Mid = userInfo.Mid,
                    Stoken = userInfo.Stoken,
                    Dest = dest,
                }, PassportJsonContext.Default.GetCrossTokenUrlBySTokenRequest),
            };
        }
        else if (userInfo.IsChinaServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api.mihoyo.com/account/ma-cn-session/app/createCrossLoginToken")
            {
                Content = JsonContent.Create(new CreateCrossLoginTokenRequest
                {
                    Stoken = userInfo.Stoken,
                    Mid = userInfo.Mid,
                    Aid = userInfo.Aid.ToString(),
                    Dest = dest,
                }, PassportJsonContext.Default.CreateCrossLoginTokenRequest),
            };
        }
        else
        {
            throw new NotSupportedException($"Passport region '{userInfo.Region}' is not supported.");
        }
        AddPassportHeaders(request, userInfo);
        var result = await SendAsync<CrossLoginTokenResult>(request, cancellationToken).ConfigureAwait(false);
        return result.Url;
    }


    /// <summary>
    /// 游戏授权登录
    /// </summary>
    public async Task<AuthTicketResult> CreateAuthTicketAsync(PassportUserInfo userInfo, string gameBiz, CancellationToken cancellationToken = default)
    {
        HttpRequestMessage request;
        if (userInfo.IsGlobalServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api-sg.hoyoverse.com/account/ma-verifier/api/createAuthTicketBySToken")
            {
                Content = JsonContent.Create(new CreateAuthTicketBySTokenRequest
                {
                    Mid = userInfo.Mid,
                    Stoken = userInfo.Stoken,
                    BizName = gameBiz,
                }, PassportJsonContext.Default.CreateAuthTicketBySTokenRequest),
            };
        }
        else if (userInfo.IsChinaServer)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api.mihoyo.com/account/ma-cn-verifier/app/createAuthTicketByGameBiz")
            {
                Content = JsonContent.Create(new CreateAuthTicketByGameBizRequest
                {
                    GameBiz = gameBiz,
                    Stoken = userInfo.Stoken,
                    Uid = userInfo.Aid,
                    Mid = userInfo.Mid,
                }, PassportJsonContext.Default.CreateAuthTicketByGameBizRequest),
            };
        }
        else
        {
            throw new NotSupportedException($"Passport region '{userInfo.Region}' is not supported.");
        }
        AddPassportHeaders(request, userInfo);
        return await SendAsync<AuthTicketResult>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 国服更换 token（强类型版本，内部走统一的 V2 换票实现）。
    /// </summary>
    /// <param name="userInfo"></param>
    /// <param name="destToken"></param>
    /// <param name="androidDevice">可选的 Android 设备信息，为空时使用 Windows（HYP）设备请求头。</param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<ExchangeTokenResponse> ExchangeTokenAsync(PassportUserInfo userInfo, TokenType destToken, AndroidDeviceInfo? androidDevice, CancellationToken cancellation = default)
    {
        if (!userInfo.IsChinaServer)
        {
            throw new NotSupportedException($"Server region '{userInfo.Region}' is not supported.");
        }
        var result = await ExchangeTokenCoreAsync(userInfo.Stoken, userInfo.Mid, (int)destToken, androidDevice, cancellation).ConfigureAwait(false);
        result.EnsureSuccess();
        return result.Deserialize<ExchangeTokenResponse>() ?? throw new miHoYoApiException(-1, "Can not parse the response body.");
    }


    /// <summary>
    /// 国际服更换 token
    /// </summary>
    /// <param name="userInfo"></param>
    /// <param name="destTokenTypes"></param>
    /// <param name="androidDevice"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<GetBySTokenResponse> GetBySTokenAsync(PassportUserInfo userInfo, IReadOnlyCollection<TokenType> destTokenTypes, AndroidDeviceInfo? androidDevice, CancellationToken cancellation = default)
    {
        if (!userInfo.IsGlobalServer)
        {
            throw new NotSupportedException($"Server region '{userInfo.Region}' is not supported.");
        }
        var requestBody = new GetBySTokenRequest { DestTokenTypes = [.. destTokenTypes] };
        var message = new HttpRequestMessage(HttpMethod.Post, "https://sg-public-api.hoyoverse.com/account/ma-passport/token/getBySToken")
        {
            Content = JsonContent.Create(requestBody, PassportJsonContext.Default.GetBySTokenRequest)
        };
        message.Headers.Add("DS", DynamicSecret.CreatePassportDS(userInfo.Region, requestBody));
        message.Headers.Add("Cookie", $"stoken={userInfo.Stoken};mid={userInfo.Mid};stuid={userInfo.Aid}");
        if (androidDevice is null)
        {
            AddPassportHeaders(message, userInfo);
        }
        else
        {
            message.Headers.Add("x-rpc-app_id", "ddxf6vlr1reo");
            message.Headers.Add("x-rpc-client_type", "2");
            message.Headers.Add("x-rpc-game_biz", "bbs_oversea");
            message.Headers.Add("x-rpc-sdk_version", GameRecordClientBase.HoyolabSdkVersion);
            message.Headers.Add("x-rpc-app_version", GameRecordClientBase.HoyolabAppVersion);
            message.Headers.Add("x-rpc-language", Language);
            message.Headers.Add("x-rpc-package_name", "com.mihoyo.hoyolab");
            message.Headers.Add("x-rpc-age_gate", "true");
            message.Headers.Add("x-rpc-age_gate_eu", "true");
            message.Headers.Add("x-rpc-age_gate_br", "true");
            if (androidDevice?.ExtFields is { } extFields)
            {
                message.Headers.Add("x-rpc-device_id", extFields.AndroidId);
                message.Headers.Add("x-rpc-device_fp", androidDevice.DeviceFp);
                message.Headers.Add("x-rpc-device_name", extFields.DeviceName.Replace(' ', '+'));
                message.Headers.Add("x-rpc-device_model", extFields.Model);
                message.Headers.Add("x-rpc-device_os", extFields.OsVersion);
            }
            else
            {
                message.Headers.Add("x-rpc-device_id", DeviceId);
                message.Headers.Add("x-rpc-device_fp", DeviceFp);
                message.Headers.Add("x-rpc-device_name", Environment.MachineName);
                message.Headers.Add("x-rpc-device_model", "x86_64");
                message.Headers.Add("x-rpc-device_os", Environment.OSVersion.Version.Build > 22000 ? "Windows 11 Version 2009" : "Windows 10 Version 1909");
            }
        }
        return await SendAsync<GetBySTokenResponse>(message, cancellation).ConfigureAwait(false);
    }


    /// <summary>
    /// 获取账号下已绑定的所有游戏角色
    /// </summary>
    /// <param name="userInfo"></param>
    /// <param name="deviceInfo"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<List<GameRecordRole>> GetUserGameRolesAsync(PassportUserInfo userInfo, AndroidDeviceInfo? deviceInfo, CancellationToken cancellation = default)
    {
        string url = userInfo.Region switch
        {
            ServerRegion.HoYoverse => $"https://api-account-os.hoyolab.com/binding/api/getUserGameRolesBySToken",
            ServerRegion.miHoYo => $"https://api-takumi.miyoushe.com/binding/api/getUserGameRolesByStoken",
            _ => throw new NotSupportedException($"Server region '{userInfo.Region}' is not supported.")
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-rpc-client_type", "2");
        request.Headers.Add("Cookie", $"stuid={userInfo.Aid};stoken={userInfo.Stoken};mid={userInfo.Mid};");
        if (userInfo.IsGlobalServer)
        {
            request.Headers.Add("x-rpc-app_id", "ddxf6vlr1reo");
            request.Headers.Add("DS", DynamicSecret.CreateAppDS2(userInfo.Region, url));
            request.Headers.Add("x-rpc-app_version", GameRecordClientBase.HoyolabAppVersion);
            request.Headers.Add("x-rpc-language", Language);
            request.Headers.Add("x-rpc-uid", userInfo.Aid.ToString());
            request.Headers.Add("User-Agent", GameRecordClientBase.HoyolabUAContent);
            request.Headers.Referrer = new Uri("https://app.hoyolab.com");
        }
        else
        {
            request.Headers.Add("x-rpc-app_id", "ddxf5qt290cg");
            request.Headers.Add("DS", DynamicSecret.CreateAppDS1(userInfo.Region));
            request.Headers.Add("x-rpc-app_version", GameRecordClientBase.HyperionAppVersion);
            request.Headers.Add("User-Agent", GameRecordClientBase.HyperionUAContent);
            request.Headers.Referrer = new Uri("https://app.mihoyo.com");
        }

        if (deviceInfo?.ExtFields is { } extFields)
        {
            request.Headers.Add("x-rpc-sys_version", extFields.OsVersion);
            request.Headers.Add("x-rpc-channel", userInfo.IsGlobalServer ? "google" : "miyousheluodi");
            request.Headers.Add("x-rpc-device_id", extFields.AndroidId);
            request.Headers.Add("x-rpc-device_fp", deviceInfo.DeviceFp);
            request.Headers.Add("x-rpc-device_name", $"{extFields.Manufacturer} {extFields.Model}");
            request.Headers.Add("x-rpc-device_model", extFields.Model);
        }
        var data = await SendAsync<GameRecordRoleWrapper>(request, cancellation).ConfigureAwait(false);
        List<GameRecordRole> list = data.List ?? [];
        foreach (var item in list)
        {
            item.Aid = userInfo.Aid;
        }
        return list;
    }




    #endregion


}
