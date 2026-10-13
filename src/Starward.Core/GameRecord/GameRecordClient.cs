using Starward.Core.GameRecord.Genshin.SpiralAbyss;
using Starward.Core.GameRecord.Genshin.TravelersDiary;
using Starward.Core.GameRecord.StarRail.ForgottenHall;
using Starward.Core.GameRecord.StarRail.PureFiction;
using Starward.Core.GameRecord.StarRail.ApocalypticShadow;
using Starward.Core.GameRecord.StarRail.SimulatedUniverse;
using Starward.Core.GameRecord.StarRail.TrailblazeCalendar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Starward.Core.GameRecord.Genshin.ImaginariumTheater;
using Starward.Core.GameRecord.ZZZ.ShiyuDefense;
using Starward.Core.GameRecord.ZZZ.DeadlyAssault;
using Starward.Core.GameRecord.ZZZ.InterKnotReport;
using Starward.Core.Gacha.ZZZ;
using Starward.Core.GameRecord.ZZZ.UpgradeGuide;
using Starward.Core.GameRecord.Genshin.DailyNote;
using Starward.Core.GameRecord.StarRail.DailyNote;
using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.GameRecord.BH3.DailyNote;
using Starward.Core.GameRecord.Genshin.StygianOnslaught;
using Starward.Core.GameRecord.ZZZ.ThresholdSimulation;
using Starward.Core.GameRecord.StarRail.ChallengePeak;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.SignIn;
using Starward.Core.Passport;





#if !DEBUG
using System.Net.Http.Json;
#endif
using System.Text.Json;

namespace Starward.Core.GameRecord;

public abstract class GameRecordClient
{


    #region Constant

    protected const string Accept = "Accept";
    protected const string Cookie = "Cookie";
    protected const string UserAgent = "User-Agent";
    protected const string X_Request_With = "X-Requested-With";
    protected const string DS = "DS";
    protected const string Referer = "Referer";
    protected const string Origin = "Origin";
    protected const string Application_Json = "application/json";
    protected const string com_mihoyo_hyperion = "com.mihoyo.hyperion";
    protected const string com_mihoyo_hoyolab = "com.mihoyo.hoyolab";
    protected const string x_rpc_app_version = "x-rpc-app_version";
    protected const string x_rpc_device_id = "x-rpc-device_id";
    protected const string x_rpc_device_fp = "x-rpc-device_fp";
    protected const string x_rpc_client_type = "x-rpc-client_type";
    protected const string x_rpc_language = "X-Rpc-Language";
    protected const string x_rpc_lang = "x-rpc-lang";
    protected const string x_rpc_device_name = "x-rpc-device_name";
    protected const string x_rpc_platform = "x-rpc-platform";
    protected const string x_rpc_sys_version = "x-rpc-sys_version";
    protected const string x_rpc_page = "x-rpc-page";
    protected const string x_rpc_tool_verison = "x-rpc-tool_verison";
    protected const string x_rpc_lrsag = "x-rpc-lrsag";
    protected const string x_rpc_geetest_ext = "x-rpc-geetest_ext";
    protected const string x_rpc_aigis = "x-rpc-aigis";
    protected const string x_rpc_challenge = "x-rpc-challenge";

    /// <summary>绝区零战绩 H5（<c>mihoyo-zzz-game-record</c>）版本，对齐米游社客户端的 <c>x-op-env</c>。</summary>
    protected const string ZzzGameRecordH5Version = "v3.0.10";

    /// <summary>绝区零战绩首页的 <c>x-rpc-page</c>。</summary>
    public const string ZzzGameRecordH5Page = "v3.0.10_#/zzz";

    /// <summary>绝区零绳网月报页的 <c>x-rpc-page</c>。</summary>
    public const string ZzzNotebookH5Page = "v3.0.10_#/zzz/notebook";

    /// <summary>绝区零在米游社的 gameId（战绩 H5 / <c>x-rpc-geetest_ext</c>）。</summary>
    protected const int ZzzGameId = 8;

    /// <summary>国际服战绩 H5 的 <c>x-rpc-sys_version</c>（与 <see cref="HoyolabClient"/> 的 UA 里 Android 13 对齐）；国服由 <see cref="HyperionClient"/> 的 <see cref="HyperionClient.RpcSysVersionValue"/> 提供。</summary>
    public const string RpcSysVersion = "13";

    #endregion



    public abstract string UAContent { get; }

    public abstract string AppVersion { get; }

    /// <summary>
    /// H5 活动页默认 Origin（国服 <c>https://act.mihoyo.com</c>，国际服 <c>https://act.hoyolab.com</c>）。
    /// </summary>
    protected abstract string ActOrigin { get; }

    /// <summary>
    /// <c>X-Requested-With</c> 包名（国服 <c>com.mihoyo.hyperion</c>，国际服 <c>com.mihoyo.hoyolab</c>）。
    /// </summary>
    protected abstract string XRequestedWithValue { get; }

    /// <summary>
    /// 战绩 H5 的 <c>x-rpc-lang</c> / <c>x-rpc-language</c>。国服固定 zh-cn；国际服用当前界面语言。
    /// </summary>
    protected virtual string RpcLanguage => "zh-cn";

    /// <summary>
    /// 战绩 H5 的 <c>x-rpc-device_name</c>。由具体客户端提供，避免与 UA 里的机型自相矛盾。
    /// </summary>
    protected abstract string RpcDeviceName { get; }

    /// <summary>
    /// 战绩 H5 的 <c>x-rpc-sys_version</c>。由具体客户端提供，避免与 UA 里的系统版本自相矛盾。
    /// </summary>
    protected abstract string RpcSysVersionValue { get; }

    public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");

    public string DeviceFp { get; set; } = "0000000000000";

    /// <summary>getFp 使用的 seed_id，需与 <see cref="DeviceFp"/> 一起稳定复用，并写入 DEVICEFP_SEED_ID Cookie。</summary>
    public string DeviceFpSeedId { get; set; } = "";

    /// <summary>getFp 使用的 seed_time（毫秒时间戳字符串），与 <see cref="DeviceFpSeedId"/> 成对复用。</summary>
    public string DeviceFpSeedTime { get; set; } = "";

    /// <summary>极验通过后重试战绩请求时带上的 <c>x-rpc-aigis</c>。</summary>
    public string? RiskAigisHeader { get; set; }

    /// <summary>极验通过后重试战绩请求时带上的 <c>x-rpc-challenge</c>。</summary>
    public string? RiskChallenge { get; set; }


    /// <summary>
    /// 申请设备指纹（<c>public-data-api.mihoyo.com/device-fp/api/getFp</c>）时附带的账号 Cookie（B 组改动）。
    /// <para>
    /// **只用于 getFp 这一个请求**：其它请求的 Cookie 一律由各自调用点显式写入，不受它影响。
    /// 目的是让服务端把这枚指纹登记到"已登录的账号"上——不带 Cookie 的匿名申请拿到的指纹没有账号可绑，
    /// 绝区零便笺/绳网月报会一直 10041；真实客户端就是在已登录的米游社环境里完成这一步的。
    /// </para>
    /// <para>
    /// 由 <c>GameRecordService.UpdateHyperionDeviceFpAsync</c> 在调用前设置、调用结束后在 <c>finally</c> 里清空，
    /// 免得泄漏到后续普通请求上。取不到账号 Cookie 时为 null，此时照旧匿名申请。
    /// </para>
    /// </summary>
    public string? GetDeviceFpCookie { get; set; }




    #region Dynamic Secret


    protected abstract string ApiSalt { get; }

    protected abstract string ApiSalt2 { get; }


    private static string GetRandomString(int timestamp)
    {
        var sb = new StringBuilder(6);
        var random = new Random(timestamp);
        for (int i = 0; i < 6; i++)
        {
            int v8 = random.Next(0, 32768) % 26;
            int v9 = 87;
            if (v8 < 10)
            {
                v9 = 48;
            }
            _ = sb.Append((char)(v8 + v9));
        }
        return sb.ToString();
    }


    protected string CreateSecret()
    {
        return CreateSecret(ApiSalt);
    }


    /// <summary>
    /// 用指定盐值生成 Gen1 DS（<c>DS</c> 请求头）。
    /// <para>
    /// 个别接口的盐与战绩接口不同（如 <c>binding/api/genAuthKey</c>），需要按接口传入。
    /// </para>
    /// </summary>
    /// <param name="salt">参与签名的盐值。</param>
    /// <returns><c>t,r,check</c> 形式的 DS 值。</returns>
    protected string CreateSecret(string salt)
    {
        var t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = GetRandomString(t);
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"salt={salt}&t={t}&r={r}"));
        var check = Convert.ToHexString(bytes).ToLower();
        return $"{t},{r},{check}";
    }


    protected string CreateSecret2(string url)
    {
        int t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = Random.Shared.Next(100000, 200000).ToString();
        string b = "";
        string q = "";
        string[] urls = url.Split('?');
        if (urls.Length == 2)
        {
            string[] queryParams = urls[1].Split('&').OrderBy(x => x).ToArray();
            q = string.Join("&", queryParams);
        }
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"salt={ApiSalt2}&t={t}&r={r}&b={b}&q={q}"));
        var check = Convert.ToHexString(bytes).ToLower();
        string result = $"{t},{r},{check}";
        return result;
    }


    protected string CreateSecret2<T>(string url, T postBody)
    {
        int t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = Random.Shared.Next(100000, 200000).ToString();
        string b = JsonSerializer.Serialize(postBody, typeof(T), GameRecordJsonContext.Default);
        string q = "";
        string[] urls = url.Split('?');
        if (urls.Length == 2)
        {
            string[] queryParams = urls[1].Split('&').OrderBy(x => x).ToArray();
            q = string.Join("&", queryParams);
        }
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"salt={ApiSalt2}&t={t}&r={r}&b={b}&q={q}"));
        var check = Convert.ToHexString(bytes).ToLower();
        string result = $"{t},{r},{check}";
        return result;
    }


    #endregion




    protected readonly HttpClient _httpClient;




    public GameRecordClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher };
    }




    #region Common Method




    /// <summary>
    /// 对齐米游社战绩 H5 的 <c>game_record/app</c> 请求头：Cookie（合并设备指纹）+ Gen2 DS + Referer + 设备指纹 + page/tool。
    /// 缺 <c>x-rpc-page</c> / <c>x-rpc-platform</c> 等时服务端易返回 10035。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="cookie">账号 Cookie；为 null 时仅写入设备指纹字段。</param>
    /// <param name="url">用于生成 DS、以及选择 <c>x-rpc-tool_verison</c> / <c>x-rpc-page</c> 的完整 URL。</param>
    /// <param name="referer">Referer，同时用于推导 Origin。</param>
    protected void AddGameRecordAppHeaders(HttpRequestMessage request, string? cookie, string url, string referer = "https://webstatic.mihoyo.com/")
    {
        request.Headers.Add(Cookie, MergeDeviceFpCookies(cookie));
        request.Headers.Add(DS, CreateSecret2(url));
        request.Headers.Add(Referer, referer);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(x_rpc_device_id, DeviceId);
        request.Headers.Add(x_rpc_device_fp, DeviceFp);
        request.Headers.TryAddWithoutValidation(x_rpc_platform, "5");
        request.Headers.TryAddWithoutValidation(x_rpc_sys_version, RpcSysVersionValue);
        // 官方 H5 对空格做百分号编码（如 Vivo%20V2329A）
        request.Headers.TryAddWithoutValidation(x_rpc_device_name, Uri.EscapeDataString(RpcDeviceName));
        ResolveGameRecordH5Identity(url, out string toolVersion, out string page);
        request.Headers.TryAddWithoutValidation(x_rpc_tool_verison, toolVersion);
        request.Headers.TryAddWithoutValidation(x_rpc_page, page);
        TryAddOriginFromReferer(request, referer);
        request.Headers.TryAddWithoutValidation(X_Request_With, XRequestedWithValue);
    }


    /// <summary>
    /// 按战绩接口路径选择官方 H5 的 <c>x-rpc-tool_verison</c> / <c>x-rpc-page</c>（字段名官方拼写就是 verison）。
    /// </summary>
    /// <param name="url">接口 URL。</param>
    /// <param name="toolVersion">输出 <c>x-rpc-tool_verison</c>。</param>
    /// <param name="page">输出 <c>x-rpc-page</c>。</param>
    private static void ResolveGameRecordH5Identity(string url, out string toolVersion, out string page)
    {
        // 星铁：抓包米游社打开 rpg/index.html 时为 v4.4.0
        if (url.Contains("/hkrpg/", StringComparison.OrdinalIgnoreCase))
        {
            toolVersion = "v4.4.0";
            page = "v4.4.0_#/rpg";
            return;
        }
        // 原神统一战绩页 v7.0.0-gr-cn
        if (url.Contains("/genshin/", StringComparison.OrdinalIgnoreCase))
        {
            toolVersion = "v7.0.0-gr-cn";
            page = "v7.0.0-gr-cn_#";
            return;
        }
        if (url.Contains("zzz", StringComparison.OrdinalIgnoreCase))
        {
            toolVersion = ZzzGameRecordH5Version;
            page = ZzzGameRecordH5Page;
            return;
        }
        toolVersion = "v7.0.0-gr-cn";
        page = "v7.0.0-gr-cn_#";
    }


    /// <summary>
    /// 从 Referer 推导 Origin，对齐官方 H5（星铁战绩 Origin 为 webstatic，而非 act）。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="referer">Referer。</param>
    private static void TryAddOriginFromReferer(HttpRequestMessage request, string referer)
    {
        if (Uri.TryCreate(referer, UriKind.Absolute, out Uri? uri))
        {
            request.Headers.TryAddWithoutValidation(Origin, uri.GetLeftPart(UriPartial.Authority));
        }
    }


    /// <summary>
    /// 对齐米游社绝区零战绩 H5（<c>act.mihoyo.com/app/mihoyo-zzz-game-record</c>）WebView 注入的请求头。
    /// 官方请求不带 DS / <c>x-rpc-client_type</c> / <c>x-rpc-tool_verison</c>；带 <c>x-rpc-geetest_ext</c> 与 <c>x-rpc-platform: 2</c>。
    /// 继续按原神/星铁战绩头发送时，服务端易返回 10041（账号存在风险）。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="role">当前游戏角色（Cookie、区服用于 geetest_ext）。</param>
    /// <param name="requestUrl">用于选择 <c>x-rpc-page</c> 的 URL；空则用 <paramref name="request"/> 的 URI。</param>
    protected void AddZZZGameRecordH5Headers(HttpRequestMessage request, GameRecordRole role, string? requestUrl = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(role);
        request.Headers.TryAddWithoutValidation(Cookie, MergeDeviceFpCookies(role.Cookie));
        string? url = requestUrl ?? request.RequestUri?.OriginalString;
        foreach (var header in GetZZZGameRecordH5InjectHeaders(role, url))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }


    /// <summary>
    /// 绝区零战绩 H5 由米游社客户端注入的请求头（不含 Cookie，Cookie 由调用方或 WebView 携带）。
    /// </summary>
    /// <param name="role">当前游戏角色。</param>
    /// <param name="requestUrl">请求 URL，用于区分首页与绳网月报的 <c>x-rpc-page</c>。</param>
    /// <returns>按官方抓包顺序的请求头。</returns>
    /// <remarks>
    /// <c>x-rpc-geetest_ext</c> 逐字段对齐官方 notebook 抓包：
    /// <c>viewUid</c> = 通行证 uid、<c>server</c> = 角色区服、<c>gameId</c> = <see cref="ZzzGameId"/>、
    /// <c>page</c> = 与 <c>x-rpc-page</c> 相同的 <see cref="ZzzNotebookH5Page"/> /
    /// <see cref="ZzzGameRecordH5Page"/>、<c>isHost</c> = 1、<c>viewSource</c> = 3、<c>actionSource</c> = 127。
    /// </remarks>
    public IReadOnlyList<KeyValuePair<string, string>> GetZZZGameRecordH5InjectHeaders(GameRecordRole role, string? requestUrl = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        string page = ResolveZZZGameRecordH5Page(requestUrl);
        string referer = ActOrigin.TrimEnd('/') + "/";
        string origin = Uri.TryCreate(referer, UriKind.Absolute, out Uri? uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : ActOrigin;
        string viewUid = GetBbsUidFromCookie(role.Cookie);
        string server = role.Region ?? "";
        string geetestExt = $$"""{"viewUid":"{{JsonEscapeHeaderValue(viewUid)}}","server":"{{JsonEscapeHeaderValue(server)}}","gameId":{{ZzzGameId}},"page":"{{JsonEscapeHeaderValue(page)}}","isHost":1,"viewSource":3,"actionSource":127}""";
        return
        [
            new(Referer, referer),
            new(Origin, origin),
            new(x_rpc_app_version, AppVersion),
            new(x_rpc_device_id, DeviceId),
            new(x_rpc_device_fp, DeviceFp),
            new(x_rpc_device_name, Uri.EscapeDataString(RpcDeviceName)),
            new(x_rpc_sys_version, RpcSysVersionValue),
            new(x_rpc_platform, "2"),
            new(x_rpc_page, page),
            new(x_rpc_lang, RpcLanguage),
            new(x_rpc_language, RpcLanguage),
            new(x_rpc_lrsag, ""),
            new(X_Request_With, XRequestedWithValue),
            new(x_rpc_geetest_ext, geetestExt),
        ];
    }


    /// <summary>
    /// 绳网月报走 notebook 页；其余绝区零战绩接口走首页。
    /// </summary>
    /// <param name="url">请求 URL。</param>
    /// <returns><c>x-rpc-page</c> 的值。</returns>
    private static string ResolveZZZGameRecordH5Page(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && url.Contains("nap_ledger", StringComparison.OrdinalIgnoreCase))
        {
            return ZzzNotebookH5Page;
        }
        return ZzzGameRecordH5Page;
    }


    /// <summary>
    /// 从 Cookie 取米游社通行证 UID，供 <c>x-rpc-geetest_ext.viewUid</c> 使用；缺失时与官方 notebook 请求一致填 <c>0</c>。
    /// </summary>
    /// <param name="cookie">账号 Cookie。</param>
    /// <returns>通行证 UID 字符串。</returns>
    private static string GetBbsUidFromCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return "0";
        }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        foreach (string key in new[] { "account_id_v2", "account_id", "ltuid_v2", "ltuid", "stuid" })
        {
            if (map.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return "0";
    }


    /// <summary>
    /// 转义写入 JSON 请求头的字符串，避免 Cookie 异常值破坏 geetest_ext。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>可安全嵌入 JSON 字符串字面量的值。</returns>
    private static string JsonEscapeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }


    protected virtual async Task<T> CommonSendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default) where T : class
    {
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        request.Headers.Add(Accept, Application_Json);
        request.Headers.Add(UserAgent, UAContent);
        TryApplyRiskControlHeaders(request);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
#if DEBUG
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseData = JsonSerializer.Deserialize(content, typeof(miHoYoApiWrapper<T>), GameRecordJsonContext.Default) as miHoYoApiWrapper<T>;
#else
        var responseData = await response.Content.ReadFromJsonAsync(typeof(miHoYoApiWrapper<T>), GameRecordJsonContext.Default, cancellationToken) as miHoYoApiWrapper<T>;
#endif
        if (responseData is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.", ParseAigis(response));
        }
        if (responseData.Retcode != 0)
        {
            throw new miHoYoApiException(responseData.Retcode, responseData.Message, ParseAigis(response));
        }
        return responseData.Data;
    }


    /// <summary>
    /// 极验通过后把 aigis / challenge 带到下一次战绩请求。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    private void TryApplyRiskControlHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(RiskAigisHeader))
        {
            request.Headers.TryAddWithoutValidation(x_rpc_aigis, RiskAigisHeader);
        }
        if (!string.IsNullOrWhiteSpace(RiskChallenge))
        {
            request.Headers.TryAddWithoutValidation(x_rpc_challenge, RiskChallenge);
        }
    }


    /// <summary>
    /// 解析风控响应头 <c>x-rpc-aigis</c>。没有或无法解析时返回 null。
    /// </summary>
    /// <param name="response">服务端响应。</param>
    /// <returns>极验载荷；不可用时为 null。</returns>
    private static PassportAigisHeader? ParseAigis(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(x_rpc_aigis, out var values))
        {
            return null;
        }
        return PassportClient.ParseAigisHeader(values.FirstOrDefault());
    }


    /// <summary>
    /// 把 getFp 得到的指纹写入 Cookie，对齐官方战绩 H5 携带的 DEVICEFP / _MHYUUID。
    /// </summary>
    /// <param name="cookie">原始 Cookie；可为 null。</param>
    /// <returns>合并设备指纹后的 Cookie。</returns>
    protected string MergeDeviceFpCookies(string? cookie)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }
        // 覆盖写入本程序的值（基线行为）：x-rpc-device_id / _MHYUUID 必须同源，指纹也一并用本程序的。
        if (!string.IsNullOrWhiteSpace(DeviceFp) && DeviceFp is not "0000000000000")
        {
            map["DEVICEFP"] = DeviceFp;
        }
        if (!string.IsNullOrWhiteSpace(DeviceFpSeedId))
        {
            map["DEVICEFP_SEED_ID"] = DeviceFpSeedId;
        }
        if (!string.IsNullOrWhiteSpace(DeviceFpSeedTime))
        {
            map["DEVICEFP_SEED_TIME"] = DeviceFpSeedTime;
        }
        if (!string.IsNullOrWhiteSpace(DeviceId))
        {
            map["_MHYUUID"] = DeviceId;
        }
        return string.Join("; ", map.Select(static kv => $"{kv.Key}={kv.Value}"));
    }




    /* 【已撤销，勿恢复】以下是 ResolveEffectiveDeviceFp / ResolveEffectiveDeviceFpSeed / GetCookiePairs 三个 helper。
       产品方确认"原神好使"的基线是直接用 DeviceFp 写头，不引入"本地优先"的解析层，故撤销（保留 MergeDeviceFpCookies 的覆盖语义）。
    /// <summary>
    /// 取 <c>x-rpc-device_fp</c> 请求头该用的指纹：**本程序登记的那一枚优先**，与写进 Cookie 的值一致。
    /// <para>
    /// 优先级与 <see cref="MergeDeviceFpCookies"/> 相同：<see cref="DeviceFp"/>（<c>Setting</c> 里本程序登记的）
    /// 赢；只有它缺失或还是占位值 <c>0000000000000</c> 时，才回落到角色 Cookie 里带的浏览器值。
    /// </para>
    /// <para>
    /// **不要把它改回"Cookie 优先"**：实机单变量实验证明 App 接口只认本地登记的那一枚，
    /// 用浏览器 Cookie 自带的那一枚（<c>38d8158030f89</c>）会被绝区零便笺判成 10041。
    /// </para>
    /// </summary>
    /// <param name="cookie">该次请求将要带上的角色 Cookie；可为 null。</param>
    /// <returns>请求头与 Cookie 应当共用的指纹。</returns>
    public string ResolveEffectiveDeviceFp(string? cookie)
    {
        if (!string.IsNullOrWhiteSpace(DeviceFp) && DeviceFp is not "0000000000000")
        {
            return DeviceFp;
        }
        var map = GetCookiePairs(cookie);
        if (map.TryGetValue("DEVICEFP", out string? fromCookie) && !string.IsNullOrWhiteSpace(fromCookie))
        {
            return fromCookie;
        }
        return DeviceFp;
    }




    /// <summary>
    /// 取 DEVICEFP 配套的 seed 值：同样是**本程序登记的值优先**，Cookie 只在本地缺失时兜底。
    /// <para>
    /// 与 <see cref="ResolveEffectiveDeviceFp"/> 一样，方向不能反过来：这套 App 接口认的是本地登记的
    /// 那一套设备身份（指纹 + seed），浏览器 Cookie 里的值只对网页有效。
    /// </para>
    /// </summary>
    /// <param name="cookie">该次请求将要带上的角色 Cookie；可为 null。</param>
    /// <returns>(生效指纹, 生效 seed_id, 生效 seed_time)。</returns>
    protected (string DeviceFp, string SeedId, string SeedTime) ResolveEffectiveDeviceFpSeed(string? cookie)
    {
        var map = GetCookiePairs(cookie);
        string deviceFp = ResolveEffectiveDeviceFp(cookie);
        string seedId = !string.IsNullOrWhiteSpace(DeviceFpSeedId)
            ? DeviceFpSeedId
            : (map.TryGetValue("DEVICEFP_SEED_ID", out string? seedIdFromCookie) ? seedIdFromCookie : "");
        string seedTime = !string.IsNullOrWhiteSpace(DeviceFpSeedTime)
            ? DeviceFpSeedTime
            : (map.TryGetValue("DEVICEFP_SEED_TIME", out string? seedTimeFromCookie) ? seedTimeFromCookie : "");
        return (deviceFp, seedId, seedTime);
    }




    /// <summary>
    /// 把分号分隔的 Cookie 解析成键值集合：每个条目只按第一个等号切分（v2 令牌的值里可能还有 <c>=</c>），
    /// 键名不区分大小写、同名键以后出现的为准。
    /// </summary>
    /// <param name="cookie">Cookie 文本；可为 null 或空白。</param>
    /// <returns>键值集合；入参为空白时是空集合。</returns>
    private static Dictionary<string, string> GetCookiePairs(string? cookie)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return map;
        }
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }
    */



    /// <summary>
    /// 【已删除，勿恢复】设备身份键的覆盖写入 helper（<c>SetDeviceFp</c>）。
    /// 撤销"本地优先"解析层后 <see cref="MergeDeviceFpCookies"/> 已直接赋值，本方法无调用点。
    /// 覆盖语义保留在 <see cref="MergeDeviceFpCookies"/> 内（那是基线行为）。
    /// </summary>
    private static void SetDeviceFp_Deleted(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        map[key] = value;
    }



    protected virtual async Task CommonSendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        _ = await CommonSendAsync<object>(request, cancellationToken);
    }


    #endregion






    /// <summary>
    /// 米游社账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <returns></returns>
    public abstract Task<GameRecordUser> GetGameRecordUserAsync(string cookie, CancellationToken cancellationToken = default);



    /// <summary>
    /// 所有游戏账号
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<List<GameRecordRole>> GetAllGameRolesAsync(string cookie, CancellationToken cancellationToken = default);


    /// <summary>
    /// 获取游戏账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="gameBiz"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<List<GameRecordRole>> GetGameRolesAsync(string cookie, GameBiz gameBiz, CancellationToken cancellationToken = default);



    /// <summary>
    /// 获取游戏账号头像
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected abstract Task<string> GetGameRoleHeadIconAsync(GameRecordRole role, CancellationToken cancellationToken = default);


    /// <summary>
    /// 更新游戏账号头像
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<GameRecordRole> UpdateGameRoleHeadIconAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        role.HeadIcon = await GetGameRoleHeadIconAsync(role, cancellationToken);
        return role;
    }



    /// <summary>
    /// 获取设备指纹信息
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<string> GetDeviceFpAsync(CancellationToken cancellationToken = default);


    /// <summary>
    /// 用米游社 stoken 调用 <c>binding/api/genAuthKey</c> 换取抽卡 authkey（Auth Key B）。
    /// <para>
    /// stoken 与 mid 由调用方给出：工具箱 Cookie 只带派生令牌（ltoken / cookie_token），
    /// 不含 stoken，凭据要从账号池或用户粘贴的完整 Cookie 里取。
    /// </para>
    /// <para>
    /// 仅国服 Hyperion 实现；国际服 HoYoLAB 没有对应接口，一律抛
    /// <see cref="NotSupportedException"/>。
    /// </para>
    /// </summary>
    /// <param name="role">游戏角色，提供 game_biz / region / uid。</param>
    /// <param name="stoken">米游社 SToken（V2）。</param>
    /// <param name="mid">与 SToken V2 搭配的 mid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>含 authkey / authkey_ver / sign_type 的结果。</returns>
    /// <exception cref="ArgumentException">stoken / mid 为空，或角色缺少 GameBiz / Region。</exception>
    /// <exception cref="NotSupportedException">当前平台不支持 stoken 换 authkey。</exception>
    public abstract Task<GameAuthKey> GenAuthKeyAsync(GameRecordRole role, string stoken, string mid, CancellationToken cancellationToken = default);




    #region BH3


    /// <summary>
    /// 崩坏3实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<BH3DailyNote> GetBH3DailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default);




    #endregion




    #region Genshin


    /// <summary>
    /// 获取原神账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<List<GameRecordRole>> GetGenshinGameRolesAsync(string cookie, CancellationToken cancellationToken = default);


    /// <summary>
    /// 深境螺旋
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<SpiralAbyssInfo> GetSpiralAbyssInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default);


    /// <summary>
    /// 旅行札记总览
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">0 当前月</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>

    public abstract Task<TravelersDiarySummary> GetTravelsDiarySummaryAsync(GameRecordRole role, int month = 0, CancellationToken cancellationToken = default);


    /// <summary>
    /// 旅行札记收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month"></param>
    /// <param name="type"></param>
    /// <param name="page">从1开始</param>
    /// <param name="limit">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回一页收入记录</returns>
    public abstract Task<TravelersDiaryDetail> GetTravelsDiaryDetailByPageAsync(GameRecordRole role, int month, int type, int page, int limit = 100, CancellationToken cancellationToken = default);


    /// <summary>
    /// 旅行札记收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month"></param>
    /// <param name="type"></param>
    /// <param name="limit">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回该月所有收入记录</returns>
    public abstract Task<TravelersDiaryDetail> GetTravelsDiaryDetailAsync(GameRecordRole role, int month, int type, int limit = 100, CancellationToken cancellationToken = default);



    /// <summary>
    /// 幻想真境剧诗
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<List<ImaginariumTheaterInfo>> GetImaginariumTheaterInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default);



    /// <summary>
    /// 幽境危战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<List<StygianOnslaughtInfo>> GetStygianOnslaughtInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default);



    /// <summary>
    /// 原神每日便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<GenshinDailyNote> GetGenshinDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default);



    #endregion




    #region StarRail


    /// <summary>
    /// 获取星穹铁道账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException">输入的 <c>cookie</c> 为空</exception>
    public abstract Task<List<GameRecordRole>> GetStarRailGameRolesAsync(string cookie, CancellationToken cancellationToken = default);


    /// <summary>
    /// 忘却之庭
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ForgottenHallInfo> GetForgottenHallInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default);


    /// <summary>
    /// 虚构叙事
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<PureFictionInfo> GetPureFictionInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default);


    /// <summary>
    /// 末日幻影
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ApocalypticShadowInfo> GetApocalypticShadowInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default);


    /// <summary>
    /// 模拟宇宙
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<SimulatedUniverseInfo> GetSimulatedUniverseInfoAsync(GameRecordRole role, bool detail = false, CancellationToken cancellationToken = default);


    /// <summary>
    /// 开拓月历总结
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">还不清楚规律，可能是 202304</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<TrailblazeCalendarSummary> GetTrailblazeCalendarSummaryAsync(GameRecordRole role, string month = "", CancellationToken cancellationToken = default);


    /// <summary>
    /// 开拓月历收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202304</param>
    /// <param name="type">1 星琼 2 星轨票</param>
    /// <param name="page">从1开始</param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回一页收入记录</returns>
    public abstract Task<TrailblazeCalendarDetail> GetTrailblazeCalendarDetailByPageAsync(GameRecordRole role, string month, int type, int page, int page_size = 100, CancellationToken cancellationToken = default);


    /// <summary>
    /// 开拓月历收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202304</param>
    /// <param name="type">1 星琼 2 星轨票</param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回该月所有收入记录</returns>
    public abstract Task<TrailblazeCalendarDetail> GetTrailblazeCalendarDetailAsync(GameRecordRole role, string month, int type, int page_size = 100, CancellationToken cancellationToken = default);



    /// <summary>
    /// 星穹铁道实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<StarRailDailyNote> GetStarRailDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default);


    /// <summary>
    /// 星穹铁道异相仲裁
    /// </summary>
    /// <param name="role"></param>
    /// <param name="scheduleType">1 当期，3 最近三期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ChallengePeakData> GetStarRailChallengePeakDataAsync(GameRecordRole role, int scheduleType, CancellationToken cancellationToken = default);



    #endregion




    #region ZZZ


    /// <summary>
    /// 式舆防卫战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ShiyuDefenseWrapper> GetShiyuDefenseInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default);


    /// <summary>
    /// 危局强袭战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<DeadlyAssaultInfo> GetDeadlyAssaultInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default);


    /// <summary>
    /// 获取绝区零账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<List<GameRecordRole>> GetZZZGameRolesAsync(string cookie, CancellationToken cancellationToken = default);


    /// <summary>
    /// 绝区零抽卡记录
    /// </summary>
    /// <param name="role"></param>
    /// <param name="gachaType">ZZZGachaType</param>
    /// <param name="endId">首次请求不传</param>
    /// <param name="language"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ZZZGachaRecordData> GetZZZGachaRecordAsync(GameRecordRole role, int gachaType, long? endId = null, string? language = null, CancellationToken cancellationToken = default);


    /// <summary>
    /// 绳网月报总结
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<InterKnotReportSummary> GetInterKnotReportSummaryAsync(GameRecordRole role, string month = "", CancellationToken cancellationToken = default);


    /// <summary>
    /// 绳网月报收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="type"></param>
    /// <param name="page">从1开始</param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回一页收入记录</returns>
    public abstract Task<InterKnotReportDetail> GetInterKnotReportDetailByPageAsync(GameRecordRole role, string month, string type, int page, int page_size = 100, CancellationToken cancellationToken = default);


    /// <summary>
    /// 绳网月报收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="type"></param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回该月所有收入记录</returns>
    public abstract Task<InterKnotReportDetail> GetInterKnotReportDetailAsync(GameRecordRole role, string month, string type, int page_size = 100, CancellationToken cancellationToken = default);




    /// <summary>
    /// 通过养成指南获取抽卡物品信息，不可用，返回未登录错误
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [Obsolete("不可用，返回未登录错误", true)]
    public Task<ZZZGachaWiki> GetZZZGachaWikiAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
        //var items = await GetZZZUpgradeGuideItemListAsync(role, cancellationToken: cancellationToken);
        //var icons = await GetZZZUpgradeGuideIconInfoAsync(role, cancellationToken);
        //var wiki = new ZZZGachaWiki
        //{
        //    Avatar = items.AvatarList.Select(x => new ZZZGachaInfo { Id = x.Id, Name = x.Name, Rarity = x.Rarity }).ToList(),
        //    Weapon = items.Weapon.Select(x => new ZZZGachaInfo { Id = x.Id, Name = x.Name, Rarity = x.Rarity, Icon = x.Icon }).ToList(),
        //    Buddy = items.BuddyList.Select(x => new ZZZGachaInfo { Id = x.Id, Name = x.Name, Rarity = x.Rarity }).ToList()
        //};
        //foreach (var item in wiki.Avatar)
        //{
        //    if (icons.AvatarIcon.TryGetValue(item.Id, out UpgradeGuidIconInfoItem? info))
        //    {
        //        item.Icon = info.SquareAvatar;
        //    }
        //}
        //foreach (var item in wiki.Buddy)
        //{
        //    if (icons.BuddyIcon.TryGetValue(item.Id, out UpgradeGuidIconInfoItem? value))
        //    {
        //        item.Icon = value.SquareAvatar;
        //    }
        //}
        //return wiki;
    }



    /// <summary>
    /// 养成指南，不可用，返回未登录错误
    /// </summary>
    /// <param name="role"></param>
    /// <param name="avatar_id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [Obsolete("不可用，返回未登录错误", true)]
    public abstract Task<UpgradeGuideItemList> GetZZZUpgradeGuideItemListAsync(GameRecordRole role, int avatar_id = 1011, CancellationToken cancellationToken = default);



    /// <summary>
    /// 养成指南，不可用，返回未登录错误
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [Obsolete("不可用，返回未登录错误", true)]
    public abstract Task<UpgradeGuidIconInfo> GetZZZUpgradeGuideIconInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default);



    /// <summary>
    /// 绝区零实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ZZZDailyNote> GetZZZDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default);



    /// <summary>
    /// 绝区零临界推演
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ThresholdSimulationAbstractInfo> GetZZZThresholdSimulationAbstractInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default);



    /// <summary>
    /// 绝区零临界推演
    /// </summary>
    /// <param name="role"></param>
    /// <param name="void_front_id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ThresholdSimulationDetailInfo> GetZZZThresholdSimulationDetailInfoAsync(GameRecordRole role, int void_front_id, CancellationToken cancellationToken = default);




    #endregion




    #region SignIn


    /// <summary>
    /// 签到接口使用的语言（CN 固定 zh-cn；OS 由 HoyolabClient 的语言头决定）
    /// </summary>
    protected virtual string SignInLanguage => "zh-cn";


    /// <summary>
    /// 获取当前角色对应的签到活动配置，不支持的游戏抛出异常。
    /// </summary>
    /// <param name="role">游戏角色，用于解析 <see cref="GameRecordRole.GameBiz"/>。</param>
    /// <returns>该游戏 + 区服对应的签到活动配置。</returns>
    /// <exception cref="miHoYoApiException">当前游戏不支持签到时抛出。</exception>
    protected static SignInActivityConfig GetSignInConfigOrThrow(GameRecordRole role)
    {
        GameBiz biz = role.GameBiz;
        bool isOversea = biz.Server is "global";
        SignInActivityConfig? config = SignInActivityConfig.FromGame(biz.Game, isOversea);
        if (config is null)
        {
            throw new miHoYoApiException(-1, $"Sign-in is not supported for game biz: {role.GameBiz}");
        }
        return config;
    }


    /// <summary>
    /// 为签到请求添加平台相关请求头（signgame / Origin / 设备信息等）。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="config">签到活动配置，提供 signgame / origin 等字段。</param>
    /// <param name="signData">是否为数据接口（home 为 false；info/sign/resign 为 true）。</param>
    protected abstract void AddSignInPlatformHeaders(HttpRequestMessage request, SignInActivityConfig config, bool signData);


    /// <summary>
    /// 本月签到奖励列表（home 接口）。
    /// </summary>
    /// <param name="role">游戏角色，提供 cookie / region / uid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当月每日奖励列表。</returns>
    public async Task<SignInReward> GetSignInRewardAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var request = new HttpRequestMessage(HttpMethod.Get, config.HomeUrl(SignInLanguage));
        request.Headers.Add(Cookie, role.Cookie);
        // 头里的指纹要与上面这行 Cookie 里的那枚一致（Cookie 里生效的优先）。
        AddSignInPlatformHeaders(request, config, signData: false);
        return await CommonSendAsync<SignInReward>(request, cancellationToken);
    }


    /// <summary>
    /// 当前签到状态（已签天数、今日是否已签等，info 接口）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到状态信息。</returns>
    public async Task<SignInRewardInfo> GetSignInInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var request = new HttpRequestMessage(HttpMethod.Get, config.InfoUrl(SignInLanguage, role.Region, role.Uid));
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: true);
        return await CommonSendAsync<SignInRewardInfo>(request, cancellationToken);
    }


    /// <summary>
    /// 补签信息（剩余补签次数、消耗货币等，resign_info 接口）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签配额与货币信息。</returns>
    public async Task<SignInResignInfo> GetSignInResignInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var request = new HttpRequestMessage(HttpMethod.Get, config.ResignInfoUrl(SignInLanguage, role.Region, role.Uid));
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: true);
        return await CommonSendAsync<SignInResignInfo>(request, cancellationToken);
    }


    /// <summary>
    /// 执行今日签到，成功返回 retcode 0；今日已签返回 -5003（由上层处理）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到结果，含风控字段。</returns>
    public async Task<SignInResult> SignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        return await PostSignInAsync(role, resign: false, cancellationToken);
    }


    /// <summary>
    /// 执行补签。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签结果，含风控字段。</returns>
    public async Task<SignInResult> ReSignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        return await PostSignInAsync(role, resign: true, cancellationToken);
    }


    /// <summary>
    /// 签到 / 补签的 POST 公共实现。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="resign">true 走补签接口，false 走签到接口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>接口返回的签到结果。</returns>
    private async Task<SignInResult> PostSignInAsync(GameRecordRole role, bool resign, CancellationToken cancellationToken)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var body = new SignInPostBody(config.ActId, role.Region, role.Uid.ToString());
        string json = JsonSerializer.Serialize(body, typeof(SignInPostBody), GameRecordJsonContext.Default);
        string url = resign ? config.ResignUrl() : config.SignUrl();
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, Application_Json),
        };
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: true);
        return await CommonSendAsync<SignInResult>(request, cancellationToken);
    }


    #endregion




    // 寰宇蝗灾
    // https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/rogue_locust?server=prod_gf_cn&role_id={uid}&need_detail=true

    // 黄金与机械
    // https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/rogue_nous?server=prod_gf_cn&role_id={uid}&need_detail=true

    // 幻想真境剧诗
    // https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/role_combat?server=cn_gf01&role_id={uid}&active=1&need_detail=true


}
