using Starward.Core.DeviceFp;
using Starward.Core.GameRecord.ZZZ.UpgradeGuide;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Starward.Core.GameRecord;

public abstract class GameRecordClientBase
{

    protected const string Accept = "Accept";
    protected const string Cookie = "Cookie";
    protected const string UserAgent = "User-Agent";
    protected const string X_Request_With = "X-Requested-With";
    protected const string DS = "DS";
    protected const string Referer = "Referer";
    protected const string application_json = "application/json";
    protected const string com_mihoyo_hyperion = "com.mihoyo.hyperion";
    protected const string com_mihoyo_hoyolab = "com.mihoyo.hoyolab";
    protected const string x_rpc_app_version = "x-rpc-app_version";
    protected const string x_rpc_device_id = "x-rpc-device_id";
    protected const string x_rpc_device_fp = "x-rpc-device_fp";
    protected const string x_rpc_client_type = "x-rpc-client_type";
    protected const string x_rpc_lang = "x-rpc-lang";
    protected const string x_rpc_language = "x-rpc-language";
    protected const string x_rpc_app_id = "x-rpc-app_id";
    protected const string x_rpc_game_biz = "x-rpc-game_biz";
    protected const string x_rpc_sdk_version = "x-rpc-sdk_version";
    protected const string x_rpc_device_model = "x-rpc-device_model";
    protected const string x_rpc_device_name = "x-rpc-device_name";
    protected const string x_rpc_sys_version = "x-rpc-sys_version";
    protected const string x_rpc_channel = "x-rpc-channel";


    public const string HyperionAppVersion = "2.119.2";

    public const string HyperionSdkVersion = "2.44.0";

    public const string HyperionUAContent = $"Mozilla/5.0 (Linux; Android 17) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Mobile Safari/537.36 miHoYoBBS/{HyperionAppVersion}";


    public const string HoyolabAppVersion = "4.24.0";

    public const string HoyolabSdkVersion = "2.56.0";

    public const string HoyolabUAContent = $"Mozilla/5.0 (Linux; Android 17) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Mobile Safari/537.36 miHoYoBBSOversea/{HoyolabAppVersion}";


    public const long AppReleaseTimestamp = 1795579200;


    public abstract string AppVersion { get; }

    public abstract string DefaultUAContent { get; }

    public abstract ServerRegion ServerRegion { get; }

    public string UAContent { get => field ?? DefaultUAContent; set; }


    public AndroidDeviceInfo? DeviceInfo
    {
        get; set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }
            field = value;
            if (value?.ExtFields is not null)
            {
                if (!string.IsNullOrWhiteSpace(value.ExtFields.DeviceInfo))
                {
                    var splits = value.ExtFields.DeviceInfo.Split('/');
                    if (splits.Length > 3)
                    {
                        string build = splits[3];
                        UAContent = DefaultUAContent.Replace("Android 17", $"Android {value.ExtFields.OsVersion}; {value.ExtFields.Model} Build/{build}");
                        return;
                    }
                }
            }
            UAContent = null!;
        }
    }


    public string Language
    {
        get => LanguageUtil.FilterLanguage(field ?? (ServerRegion is ServerRegion.miHoYo ? "zh-cn" : "en-us"));
        set => field = LanguageUtil.FilterLanguage(value);
    }


    protected readonly HttpClient _httpClient;


    public GameRecordClientBase(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher };
    }




    protected virtual void AddDeviceHeaders(HttpRequestMessage request)
    {
        if (DeviceInfo is { } deviceInfo)
        {
            AddHeader(request, x_rpc_device_id, deviceInfo.BbsDeviceId);
            AddHeader(request, x_rpc_device_fp, deviceInfo.DeviceFp);
            AddHeader(request, x_rpc_device_name, GetDeviceName(deviceInfo.ExtFields));
            AddHeader(request, x_rpc_device_model, deviceInfo.ExtFields?.Model);
            AddHeader(request, x_rpc_sys_version, deviceInfo.ExtFields?.OsVersion);
        }
        if (ServerRegion is ServerRegion.miHoYo)
        {
            AddHeader(request, x_rpc_channel, "miyousheluodi");
        }
        if (ServerRegion is ServerRegion.HoYoverse)
        {
            AddHeader(request, x_rpc_channel, "google");
            AddHeader(request, x_rpc_lang, Language);
            AddHeader(request, x_rpc_language, Language);
        }
    }


    private static string? GetDeviceName(AndroidDeviceExtFields? extFields)
    {
        if (extFields is null)
        {
            return null;
        }
        string manufacturer = extFields.Manufacturer ?? "";
        string model = extFields.Model ?? "";
        string result = model;
        if (!model.StartsWith(manufacturer, StringComparison.Ordinal))
        {
            result = $"{manufacturer} {model}";
        }
        if (result.Length > 0)
        {
            result = result.Substring(0, 1).ToUpper() + result.Substring(1);
        }
        return Uri.EscapeDataString(result);
    }



    protected static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.Add(name, value);
        }
    }



    protected virtual async Task<T> CommonSendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default) where T : class
    {
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        request.Headers.Add(Accept, application_json);
        request.Headers.Add(UserAgent, UAContent);
        AddDeviceHeaders(request);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseData = await response.Content.ReadFromJsonAsync(typeof(miHoYoApiWrapper<T>), GameRecordJsonContext.Default, cancellationToken).ConfigureAwait(false) as miHoYoApiWrapper<T>;
        if (responseData is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.");
        }
        if (responseData.Retcode != 0)
        {
            throw new miHoYoApiException(responseData.Retcode, responseData.Message);
        }
        return responseData.Data;
    }



    #region stoken 换抽卡 authkey / Gen1 DS


    /// <summary>
    /// <c>binding/api/genAuthKey</c> 的 Gen1 DS 盐，与战绩 / 签到接口的盐都不同。
    /// </summary>
    protected const string GenAuthKeyApiSalt = "d9200c846b10886e8c874fc33c8f308b";


    /// <summary>
    /// <c>binding/api/genAuthKey</c> 的请求地址（国服）。
    /// </summary>
    protected const string GenAuthKeyApiUrl = "https://api-takumi.mihoyo.com/binding/api/genAuthKey";


    /// <summary>
    /// 用指定盐值生成 Gen1 DS（<c>DS</c> 请求头）。
    /// <para>
    /// 个别接口的盐值与常规接口不同（如 <c>binding/api/genAuthKey</c>），需要按接口传入。
    /// </para>
    /// </summary>
    /// <param name="salt">参与签名的盐值。</param>
    /// <returns><c>t,r,check</c> 形式的 DS 值。</returns>
    protected static string CreateSecret(string salt)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = RandomNumberGenerator.GetString("0123456789abcdefghijklmnop", 6);
        byte[] md5 = MD5.HashData(Encoding.UTF8.GetBytes($"salt={salt}&t={t}&r={r}"));
        return $"{t},{r},{Convert.ToHexStringLower(md5)}";
    }


    /// <summary>
    /// 用米游社 stoken 调用 <c>binding/api/genAuthKey</c> 换取抽卡 authkey（Auth Key B）。
    /// <para>
    /// stoken 与 mid 由调用方给出：工具箱 Cookie 只带派生令牌（ltoken / cookie_token），
    /// 不含 stoken，凭据要从账号池或用户粘贴的完整 Cookie 里取。
    /// </para>
    /// <para>
    /// 仅国服 Hyperion 客户端实现；国际服 HoYoLAB 没有对应接口，默认抛
    /// <see cref="NotSupportedException"/>。这里用 virtual + 默认实现而不是 abstract，
    /// 免得 6 个 HoYoLAB 客户端都要写一遍。
    /// </para>
    /// </summary>
    /// <param name="role">游戏角色，提供 game_biz / region / uid。</param>
    /// <param name="stoken">米游社 SToken（V2）。</param>
    /// <param name="mid">与 SToken V2 搭配的 mid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>含 authkey / authkey_ver / sign_type 的结果。</returns>
    /// <exception cref="NotSupportedException">当前平台不支持 stoken 换 authkey。</exception>
    public virtual Task<GameAuthKey> GenAuthKeyAsync(GameRecordRole role, string stoken, string mid, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Generating a gacha authkey from an SToken is not supported on this platform.");
    }


    /// <summary>
    /// <c>binding/api/genAuthKey</c> 的国服实现，供各国服 Hyperion 客户端复用。
    /// <para>
    /// 与社区实现一致：只带 <c>stoken</c> + <c>mid</c> 两个 cookie 键，避免无关键干扰；
    /// 该接口的 Gen1 DS 盐与战绩接口不同，须单独传入；<c>auth_appid</c> 固定
    /// <c>webview_gacha</c>（游戏内抽卡记录）。设备 id / fp 由
    /// <see cref="CommonSendAsync{T}"/> → <see cref="AddDeviceHeaders"/> 按
    /// <see cref="DeviceInfo"/> 自动补齐，与既有请求保持一致。
    /// </para>
    /// </summary>
    /// <param name="role">游戏角色，提供 game_biz / region / uid。</param>
    /// <param name="stoken">米游社 SToken（V2）。</param>
    /// <param name="mid">与 SToken V2 搭配的 mid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>含 authkey / authkey_ver / sign_type 的结果。</returns>
    /// <exception cref="ArgumentException">stoken / mid 为空，或角色缺少 GameBiz / Region。</exception>
    protected async Task<GameAuthKey> GenAuthKeyCoreAsync(GameRecordRole role, string stoken, string mid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (string.IsNullOrWhiteSpace(stoken) || string.IsNullOrWhiteSpace(mid))
        {
            throw new ArgumentException("stoken and mid are required.", nameof(stoken));
        }
        if (string.IsNullOrWhiteSpace(role.GameBiz) || string.IsNullOrWhiteSpace(role.Region))
        {
            throw new ArgumentException("GameBiz and Region are required.", nameof(role));
        }

        var body = new GenAuthKeyPostBody("webview_gacha", role.GameBiz, role.Uid, role.Region);
        string json = JsonSerializer.Serialize(body, typeof(GenAuthKeyPostBody), GameRecordJsonContext.Default);
        var request = new HttpRequestMessage(HttpMethod.Post, GenAuthKeyApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, application_json),
        };
        request.Headers.Add(Cookie, $"stoken={stoken};mid={mid}");
        request.Headers.Add(Referer, "https://app.mihoyo.com");
        request.Headers.Add(DS, CreateSecret(GenAuthKeyApiSalt));
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        return await CommonSendAsync<GameAuthKey>(request, cancellationToken).ConfigureAwait(false);
    }


    #endregion




    #region 养成指南 / badge 接口


    /// <summary>
    /// 养成指南 H5 接口使用桌面浏览器 UA（勿用 BBS 手机 UA，易触发 10035 极验风控）。
    /// </summary>
    protected const string CultivateToolUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";


    /// <summary>
    /// 国际服 act 接口 Gen1 DS 盐（与 BBS 接口的盐不同，对齐 genshin.py 的 OVERSEAS 值）。
    /// </summary>
    protected const string CultivateToolDsSaltOverseas = "6s25p5ox5y14umn1p61aqyyvbvvl3lrt";


    /// <summary>
    /// 国服 act 接口 Gen2 DS 盐（与米游社网页接口的 WebSalt2 相同）。
    /// </summary>
    private const string CultivateToolDsSaltCN = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";


    /// <summary>
    /// 生成养成指南接口的 Gen1 DS（<c>DS</c> 请求头），用于国际服 badge 登录。
    /// <para>
    /// 与 <see cref="CreateSecret(string)"/> 是同一套 Gen1 算法，只为既有调用保留这个名字。
    /// </para>
    /// </summary>
    /// <param name="salt">参与签名的盐值。</param>
    /// <returns><c>t,r,check</c> 形式的 DS 值。</returns>
    protected static string CreateCultivateSecret1(string salt)
    {
        return CreateSecret(salt);
    }


    /// <summary>
    /// 生成养成指南接口的 Gen2 DS（<c>DS</c> 请求头），请求体参与签名，用于国服 badge 登录。
    /// </summary>
    /// <typeparam name="T">请求体类型。</typeparam>
    /// <param name="url">完整请求地址（含查询参数时按官方规则排序后参与签名）。</param>
    /// <param name="postBody">POST 请求体，序列化结果参与签名。</param>
    /// <returns><c>t,r,check</c> 形式的 DS 值。</returns>
    protected static string CreateCultivateSecret2<T>(string url, T postBody)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int r = Random.Shared.Next(100000, 200001);
        string b = JsonSerializer.Serialize(postBody, typeof(T), GameRecordJsonContext.Default);
        string q = "";
        int index = url.IndexOf('?');
        if (index >= 0 && url.Length > index + 1)
        {
            q = string.Join("&", url[(index + 1)..].Split('&').Order());
        }
        byte[] md5 = MD5.HashData(Encoding.UTF8.GetBytes($"salt={CultivateToolDsSaltCN}&t={t}&r={r}&b={b}&q={q}"));
        return $"{t},{r},{Convert.ToHexStringLower(md5)}";
    }


    /// <summary>
    /// 养成指南 / badge 接口发送：使用浏览器 UA，避免 BBS 客户端标识触发 act 风控。
    /// </summary>
    /// <typeparam name="T">响应数据类型。</typeparam>
    /// <param name="request">待发送的请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应 data。</returns>
    protected async Task<T> CommonSendCultivateAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default) where T : class
    {
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        request.Headers.TryAddWithoutValidation(Accept, application_json);
        // 覆盖或补齐为桌面浏览器 UA（勿用 miHoYoBBS 手机 UA）
        request.Headers.Remove(UserAgent);
        request.Headers.TryAddWithoutValidation(UserAgent, CultivateToolUserAgent);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseData = await response.Content.ReadFromJsonAsync(typeof(miHoYoApiWrapper<T>), GameRecordJsonContext.Default, cancellationToken).ConfigureAwait(false) as miHoYoApiWrapper<T>;
        if (responseData is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.");
        }
        if (responseData.Retcode != 0)
        {
            throw new miHoYoApiException(responseData.Retcode, responseData.Message);
        }
        return responseData.Data;
    }


    /// <summary>
    /// 养成指南 badge 登录发送：与 <see cref="CommonSendCultivateAsync{T}"/> 相同 UA，并返回响应以便合并 Set-Cookie。
    /// </summary>
    /// <param name="request">待发送的请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应包装与原始响应（调用方负责读取 Set-Cookie）。</returns>
    protected async Task<(miHoYoApiWrapper<object>? Wrapper, HttpResponseMessage Response)> SendCultivateBadgeLoginAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        request.Headers.TryAddWithoutValidation(Accept, application_json);
        request.Headers.Remove(UserAgent);
        request.Headers.TryAddWithoutValidation(UserAgent, CultivateToolUserAgent);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var wrapper = JsonSerializer.Deserialize(content, typeof(miHoYoApiWrapper<object>), GameRecordJsonContext.Default) as miHoYoApiWrapper<object>;
        return (wrapper, response);
    }


    /// <summary>
    /// 将响应 <c>Set-Cookie</c> 中的键值合并进基础 Cookie 串（同名覆盖）。
    /// </summary>
    /// <param name="baseCookie">原始 Cookie；可为 null。</param>
    /// <param name="response">服务端响应。</param>
    /// <returns>合并后的 Cookie。</returns>
    protected static string MergeCookieFromSetCookie(string? baseCookie, HttpResponseMessage response)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(baseCookie))
        {
            foreach (string part in baseCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
                }
            }
        }
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            foreach (string sc in setCookies)
            {
                // 仅取第一段 name=value，忽略 Path/Expires 等属性
                string segment = sc.Split(';', 2)[0];
                int eq = segment.IndexOf('=');
                if (eq > 0)
                {
                    map[segment[..eq].Trim()] = segment[(eq + 1)..].Trim();
                }
            }
        }
        return string.Join("; ", map.Select(static kv => $"{kv.Key}={kv.Value}"));
    }


    #endregion



}
