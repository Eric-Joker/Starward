using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Starward.Core.MiyoLive;

/// <summary>
/// 米哈游前瞻直播（miyolive）公开接口：发现 act_id、拉直播元数据与兑换码。
/// 无 Cookie；只访问 <c>*.mihoyo.com</c> 官方活动 / 社区域。
/// </summary>
/// <remarks>
/// 相对上游（Moonward）删除了「米游社首页导航」这条备用发现路径：
/// 该路径请求 <c>https://bbs-api.miyoushe.com/apihub/api/home/new</c>，不在本次移植允许的
/// 主机白名单内，因此只保留 <c>bbs-api.mihoyo.com</c> 的官方动态列表（painter）一条路径。
/// </remarks>
public partial class MiyoLiveClient
{

    /// <summary>官方动态列表（painter）；仅国服活动域。</summary>
    private const string PainterHost = "https://bbs-api.mihoyo.com";

    /// <summary>miyolive index（取 code_ver）。</summary>
    private const string TakumiHost = "https://api-takumi.mihoyo.com";

    /// <summary>miyolive refreshCode（取兑换码明文）。</summary>
    private const string TakumiStaticHost = "https://api-takumi-static.mihoyo.com";

    private readonly HttpClient _httpClient;


    /// <summary>
    /// 初始化 miyolive Client。
    /// </summary>
    /// <param name="httpClient">可选共享 HttpClient；为 null 时自建并开启自动解压。</param>
    public MiyoLiveClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
    }


    /// <summary>
    /// 从官方账号动态列表中解析当次直播 <c>act_id</c>。
    /// </summary>
    /// <param name="uid">官方账号 UID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>act_id；未找到返回 null。</returns>
    public async Task<string?> TryGetActIdFromUserInstantAsync(long uid, CancellationToken cancellationToken = default)
    {
        string url = $"{PainterHost}/painter/api/user_instant/list?offset=0&size=20&uid={uid}";
        MiyoLiveUserInstantListData data = await CommonGetAsync(url, null, MiyoLiveJsonContext.Default.miHoYoApiWrapperMiyoLiveUserInstantListData, cancellationToken);
        foreach (MiyoLiveUserInstantItem item in data.List)
        {
            MiyoLivePost? post = item.Post?.Post;
            if (post is null || string.IsNullOrEmpty(post.StructuredContent))
            {
                continue;
            }

            string content = post.StructuredContent;
            string subject = post.Subject ?? "";

            // 优先：正文含官方直播页链接
            if (ContainsLivePath(content))
            {
                string? actId = ExtractActId(content);
                if (!string.IsNullOrEmpty(actId))
                {
                    return actId;
                }
            }

            // 次选：标题像前瞻/直播，再抠通用 act_id
            if (IsLikelyLivestreamPost(subject))
            {
                string? actId = ExtractActId(content);
                if (!string.IsNullOrEmpty(actId))
                {
                    return actId;
                }
            }
        }
        return null;
    }


    /// <summary>
    /// 拉取直播活动 index（含 <c>code_ver</c>）。
    /// </summary>
    /// <param name="actId">活动 act_id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>index data。</returns>
    public async Task<MiyoLiveIndexData> GetLiveIndexAsync(string actId, CancellationToken cancellationToken = default)
    {
        string url = $"{TakumiHost}/event/miyolive/index";
        return await CommonGetAsync(url, actId, MiyoLiveJsonContext.Default.miHoYoApiWrapperMiyoLiveIndexData, cancellationToken);
    }


    /// <summary>
    /// 刷新直播兑换码列表。
    /// </summary>
    /// <param name="actId">活动 act_id。</param>
    /// <param name="version"><c>code_ver</c>。</param>
    /// <param name="unixTimeSeconds">请求时间戳（秒）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>兑换码 data。</returns>
    public async Task<MiyoLiveCodeData> RefreshCodeAsync(string actId, string version, long unixTimeSeconds, CancellationToken cancellationToken = default)
    {
        string url = $"{TakumiStaticHost}/event/miyolive/refreshCode?version={Uri.EscapeDataString(version)}&time={unixTimeSeconds}";
        return await CommonGetAsync(url, actId, MiyoLiveJsonContext.Default.miHoYoApiWrapperMiyoLiveCodeData, cancellationToken);
    }


    private async Task<T> CommonGetAsync<T>(string url, string? actId, JsonTypeInfo<miHoYoApiWrapper<T>> typeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(actId))
        {
            request.Headers.TryAddWithoutValidation("x-rpc-act_id", actId);
        }
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        miHoYoApiWrapper<T>? wrapper = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
        if (wrapper is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.");
        }
        if (wrapper.Retcode != 0)
        {
            throw new miHoYoApiException(wrapper.Retcode, wrapper.Message);
        }
        return wrapper.Data ?? throw new miHoYoApiException(-1, "Response data is null.");
    }


    /// <summary>
    /// 从文本中提取 act_id（优先 live 页路径，再通用参数）。
    /// </summary>
    internal static string? ExtractActId(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        Match live = LiveActIdRegex().Match(text);
        if (live.Success)
        {
            return live.Groups[1].Value;
        }

        Match generic = GenericActIdRegex().Match(text);
        if (generic.Success)
        {
            return generic.Groups[1].Value;
        }

        return null;
    }


    private static bool ContainsLivePath(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("bbs/event/live", StringComparison.OrdinalIgnoreCase);


    private static bool IsLikelyLivestreamPost(string text) =>
        text.Contains("前瞻", StringComparison.Ordinal)
        || text.Contains("特别节目", StringComparison.Ordinal)
        || text.Contains("直播", StringComparison.Ordinal)
        || text.Contains("讨论活动", StringComparison.Ordinal);


    [GeneratedRegex(@"bbs/event/live/index\.html\?act_id=([A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveActIdRegex();


    [GeneratedRegex(@"act_id=([A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericActIdRegex();

}
