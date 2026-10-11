using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Starward.Core.Passport;

/// <summary>
/// 米哈游通行证（国服）客户端。
/// <para>
/// 只包含国服（<c>mihoyo.com</c>）接口。受限原则：每个业务动作只发出必要的请求，
/// 设备指纹等可复用数据由调用方统一缓存后注入（与米游社工具箱共用）。
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
    public const string BbsVersion = "2.115.0";


    /// <summary>DS2 签名使用的 salt（与客户端类型 5 / 4 通用，不随版本变化）。</summary>
    private const string ApiSalt2 = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";


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




    /// <summary>设备 ID，与米游社工具箱共用。</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");


    /// <summary>设备指纹，与米游社工具箱共用。</summary>
    public string DeviceFp { get; set; } = "0000000000000";




    #region 请求基础




    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };


    /// <summary>
    /// 发送请求并解析返回。所有通行证接口都使用同一套返回结构，
    /// 且人机验证（-3101）、新设备校验（-3235）需要读取响应头，因此这里不抛异常，由调用方决定如何处理。
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
    /// 使用 SToken 换取 LToken（V1）。LToken 会随时间失效，需要定期重新获取。
    /// </summary>
    public async Task<PassportResult> GetLTokenBySTokenAsync(string stoken, string? mid = null, long aid = 0, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{HostPassport}/account/auth/api/getLTokenBySToken");
        request.Headers.TryAddWithoutValidation("Cookie", CreateTokenCookie(stoken, mid, aid));
        AddBbsAppHeaders(request);
        return await SendAsync(request, cancellationToken);
    }




    /// <summary>
    /// 使用 SToken 换取 CookieToken。CookieToken 同样会随时间失效。
    /// </summary>
    public async Task<PassportResult> GetCookieTokenBySTokenAsync(string stoken, string? mid = null, long aid = 0, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{HostPassport}/account/auth/api/getCookieAccountInfoBySToken");
        request.Headers.TryAddWithoutValidation("Cookie", CreateTokenCookie(stoken, mid, aid));
        AddBbsAppHeaders(request);
        return await SendAsync(request, cancellationToken);
    }


    private static string CreateTokenCookie(string stoken, string? mid, long aid)
    {
        var items = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(mid))
        {
            items.Add($"mid={mid}");
        }
        if (aid > 0)
        {
            items.Add($"stuid={aid}");
        }
        items.Add($"stoken={stoken}");
        return string.Join("; ", items);
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
                ["token_type"] = 1,
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


}
