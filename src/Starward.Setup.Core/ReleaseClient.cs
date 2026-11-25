using Starward.Setup.Core.Github;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Setup.Core;

public class ReleaseClient
{

    /// <summary>
    /// 发布 Starward 的 GitHub 仓库所有者。版本信息与安装包地址全部取自这个仓库。
    /// </summary>
    private const string GithubOwner = "Eric-Joker";

    /// <summary>见 <see cref="GithubOwner"/>。</summary>
    private const string GithubRepository = "Starward";

    /// <summary>GitHub releases API 前缀。</summary>
    private const string GithubReleasesApi = $"https://api.github.com/repos/{GithubOwner}/{GithubRepository}/releases";

    /// <summary>一次列出的 release 条数上限（足够覆盖若干版本，含预览版）。</summary>
    private const int GithubReleasePageSize = 100;


    /// <summary>
    /// HttpClient 的兜底 BaseAddress。
    /// <para>
    /// 历史上这里指向作者自建的发布服务，本类用相对路径（<c>/release/latest</c> 等）向它取版本信息；
    /// 现在版本信息一律用 GitHub API 的绝对地址，所以这个值只剩「万一有相对路径也不会发到旧主机」的作用，
    /// 自建构建仍可通过该属性重定向。
    /// </para>
    /// </summary>
    public static Uri DefaultBaseAddress { get; set; } = new("https://api.github.com");


    private readonly HttpClient _httpClient;

    public ReleaseClient(HttpClient httpClient)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                EnableMultipleHttp2Connections = true,
                EnableMultipleHttp3Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        }
        else
        {
            _httpClient = httpClient;
        }
        _httpClient.BaseAddress = DefaultBaseAddress;
        // GitHub 的 REST API 强制要求 User-Agent，缺失时直接返回 403 且响应体不是我们期望的 JSON。
        // 这里不依赖调用方的 HttpClient 配置（publish.cs 用的是自己的那个），本类必须自己保证这个头存在。
        // 调用方用 Add 设过同名头时这里不会重复添加，也不会覆盖调用方设置的值。
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
    }


    /// <summary>
    /// 本类发出的 GitHub API 请求所用的 User-Agent。
    /// <para>GitHub 要求非空且形如 <c>product/version</c>（有评论时用 <c>product/version (comment)</c>），否则返回 403。</para>
    /// </summary>
    private static string DefaultUserAgent { get; } = CreateDefaultUserAgent();


    /// <summary>
    /// 按 GitHub 的建议生成 User-Agent，进程名取不到时退化成一个固定值。
    /// </summary>
    /// <returns>形如 <c>Starward/0.18.2</c> 的字符串。</returns>
    private static string CreateDefaultUserAgent()
    {
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        return string.IsNullOrWhiteSpace(name) ? "Starward/*" : $"{name}/*";
    }


    /// <summary>
    /// 取最新的一个 release（含预览版判断），并映射成 <see cref="ReleaseInfo"/>。
    /// </summary>
    /// <param name="isPrerelease">允许预览版；为 false 时只认正式版。</param>
    /// <param name="currentVersion">当前版本号，仅作兼容保留（GitHub 不接受这个查询参数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>版本信息。</returns>
    /// <exception cref="HttpRequestException">GitHub API 返回非 2xx（例如限流 403）。</exception>
    /// <exception cref="JsonException">响应不是可反序列化的 JSON。</exception>
    public async Task<ReleaseInfo> GetLatestReleaseInfoAsync(bool isPrerelease, string currentVersion, CancellationToken cancellationToken = default)
    {
        GithubRelease? release = await GetLatestGithubReleaseAsync(isPrerelease, cancellationToken);
        if (release is null)
        {
            // 「还没有任何 release」是合法状态（首次发布就是如此），不是错误。
            // 返回一个空的版本信息会让调用方的 TryGetReleaseInfoDetail 返回 false，各自决定怎么处理。
            return new ReleaseInfo
            {
                Version = string.Empty,
                Releases = new Dictionary<string, ReleaseInfoDetail>(),
            };
        }
        return CreateReleaseInfo(release);
    }


    /// <summary>
    /// 取最新 release 中指定「架构 + 安装类型」的那一份，并映射成 <see cref="ReleaseInfoDetail"/>。
    /// </summary>
    /// <param name="isPrerelease">允许预览版；为 false 时只认正式版。</param>
    /// <param name="currentVersion">当前版本号，仅作兼容保留。</param>
    /// <param name="arch">进程架构。</param>
    /// <param name="type">安装类型（安装版 / 便携版）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该平台的版本信息明细。</returns>
    /// <exception cref="InvalidOperationException">仓库里没有任何 release。</exception>
    /// <exception cref="PlatformNotSupportedException">最新 release 里没有该平台的安装包。</exception>
    public async Task<ReleaseInfoDetail> GetLatestReleaseInfoDetailAsync(bool isPrerelease, string currentVersion, Architecture arch, InstallType type, CancellationToken cancellationToken = default)
    {
        GithubRelease? release = await GetLatestGithubReleaseAsync(isPrerelease, cancellationToken);
        if (release is null)
        {
            throw new InvalidOperationException($"No release was found in {GithubReleasesApi} (isPrerelease: {isPrerelease}).");
        }
        ReleaseInfo info = CreateReleaseInfo(release);
        if (info.TryGetReleaseInfoDetail(arch, type, out ReleaseInfoDetail? detail))
        {
            return detail;
        }
        throw new PlatformNotSupportedException($"Platform ({arch}, {type}) is not supported.");
    }


    /// <summary>
    /// 取指定版本的 release（标签可带或不带 <c>v</c> 前缀），并映射成 <see cref="ReleaseInfo"/>。
    /// </summary>
    /// <param name="version">版本号，例如 <c>1.14.0</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>版本信息。</returns>
    public async Task<ReleaseInfo> GetReleaseInfoAsync(string version, CancellationToken cancellationToken = default)
    {
        GithubRelease? release = await GetGithubReleaseByVersionAsync(version, cancellationToken);
        if (release is null)
        {
            throw new NullReferenceException($"Cannot get json content from '{GithubReleasesApi}/tags/{version}'.");
        }
        return CreateReleaseInfo(release);
    }


    public async Task<ReleaseManifest?> GetReleaseManifestAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            // release 里没挂文件级清单（GitHub 上默认就连一个都没有），此时没有清单可下，
            // 属于「没有」而不是「失败」，交给调用方跳过按文件更新。
            return null;
        }
        return await GetFromGithubJsonAsync(url, ReleaseJsonContext.Default.ReleaseManifest, cancellationToken);
    }



    #region Release to ReleaseInfo mapping


    /// <summary>
    /// 取最新 release：正式版请求必须跳过预览版，预览版请求则可以用最新的那条。
    /// </summary>
    /// <param name="isPrerelease">允许预览版。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>GitHub release；仓库里一条 release 都没有（或没有符合条件的）时为 null。</returns>
    /// <exception cref="HttpRequestException">GitHub API 返回非 2xx。</exception>
    /// <exception cref="JsonException">响应体不是可反序列化的 JSON。</exception>
    private async Task<GithubRelease?> GetLatestGithubReleaseAsync(bool isPrerelease, CancellationToken cancellationToken)
    {
        string url = $"{GithubReleasesApi}?per_page={GithubReleasePageSize}";
        List<GithubRelease>? list = await GetFromGithubJsonAsync(url, ReleaseJsonContext.Default.ListGithubRelease, cancellationToken);
        if (list is null or { Count: 0 })
        {
            // 仓库里还没有任何 release：这是首次发布的正常状态，不是错误。
            // 以前这里直接抛异常，导致第一次发布在算 diff 基线时就失败了。
            return null;
        }
        // 未认证请求本来也看不到 draft，这里再滤一次，避免以后带 token 时把草稿当成正式版
        return list.FirstOrDefault(x => !x.Draft && (isPrerelease || !x.Prerelease));
    }


    /// <summary>
    /// 向 GitHub 发一次 GET 并把响应当成 JSON 反序列化。
    /// <para>
    /// 与 <see cref="System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync{TValue}(HttpClient, string, System.Text.Json.Serialization.Metadata.JsonTypeInfo{TValue}, CancellationToken)"/>
    /// 的区别是错误信息里带上 <b>HTTP 状态码</b> 和一小段响应体，这样 403（缺 User-Agent / 限流）
    /// 之类的失败一眼就能看出来；响应体不是 JSON 时也不会只剩一句「Cannot get json content」。
    /// </para>
    /// </summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="url">绝对地址。</param>
    /// <param name="jsonTypeInfo">源生成的反序列化元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反序列化结果；响应体是 JSON 的 <c>null</c> 时为 null。</returns>
    /// <exception cref="HttpRequestException">状态码不是 2xx。</exception>
    /// <exception cref="JsonException">响应体不是可反序列化的 JSON。</exception>
    private async Task<T?> GetFromGithubJsonAsync<T>(string url, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendGithubRequestAsync(url, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateResponseException(response, body);
        }
        T? result;
        try
        {
            result = JsonSerializer.Deserialize(body, jsonTypeInfo);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Cannot parse json content from '{url}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {DescribeBody(body)}", ex);
        }
        return result;
    }


    /// <summary>
    /// 发一次带 User-Agent 的 GitHub GET 请求并读回完整响应（不在这里判断状态码）。
    /// </summary>
    /// <param name="url">绝对地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>HTTP 响应，由调用方释放。</returns>
    private Task<HttpResponseMessage> SendGithubRequestAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 再按单个请求设一次，确保调用方传进来的 HttpClient 即便被改成不带这个头也仍然合法。
        foreach (string value in EnumerateUserAgentValues())
        {
            request.Headers.TryAddWithoutValidation("User-Agent", value);
        }
        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }


    /// <summary>
    /// 本类允许出现在 User-Agent 上的取值：优先用客户端已配置的值，其次是 <see cref="DefaultUserAgent"/>。
    /// </summary>
    /// <returns>1 或 2 个取值。</returns>
    private IEnumerable<string> EnumerateUserAgentValues()
    {
        if (_httpClient.DefaultRequestHeaders.TryGetValues("User-Agent", out IEnumerable<string>? values))
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
        else
        {
            yield return DefaultUserAgent;
        }
    }


    /// <summary>
    /// 给非 2xx 的响应造一个带状态码和响应体片段的异常。
    /// </summary>
    /// <param name="response">响应。</param>
    /// <param name="body">已读出的响应体。</param>
    /// <returns>异常。</returns>
    private static HttpRequestException CreateResponseException(HttpResponseMessage response, string? body)
    {
        return new HttpRequestException(
            $"GitHub API request to '{response.RequestMessage?.RequestUri}' failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {DescribeBody(body)}",
            null,
            response.StatusCode);
    }


    /// <summary>
    /// 把响应体裁成一行短摘要，用于异常信息；空响应体也给出一个明确的说明。
    /// </summary>
    /// <param name="body">响应体。</param>
    /// <returns>一行摘要。</returns>
    private static string DescribeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "The response body is empty.";
        }
        string text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int MaxLength = 300;
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength] + "...";
        }
        return $"Response body: {text}";
    }


    /// <summary>
    /// 按版本号找 release：先按标签直接取（标签可能带 <c>v</c> 前缀），取不到再列一遍按标签比对。
    /// </summary>
    /// <param name="version">版本号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>GitHub release；找不到时为 null。</returns>
    private async Task<GithubRelease?> GetGithubReleaseByVersionAsync(string version, CancellationToken cancellationToken)
    {
        foreach (string tag in EnumerateVersionTags(version))
        {
            string url = $"{GithubReleasesApi}/tags/{Uri.EscapeDataString(tag)}";
            GithubRelease? byTag = await GetFromGithubJsonOrNullOnNotFoundAsync(url, ReleaseJsonContext.Default.GithubRelease, cancellationToken);
            if (byTag is not null)
            {
                return byTag;
            }
        }
        string listUrl = $"{GithubReleasesApi}?per_page={GithubReleasePageSize}";
        List<GithubRelease>? list = await GetFromGithubJsonAsync(listUrl, ReleaseJsonContext.Default.ListGithubRelease, cancellationToken);
        return list?.FirstOrDefault(x => !x.Draft && string.Equals(NormalizeVersion(x.TagName), NormalizeVersion(version), StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// 与 <see cref="GetFromGithubJsonAsync{T}"/> 相同，但 404 视为「没有」返回 null 而不是抛异常。
    /// </summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="url">绝对地址。</param>
    /// <param name="jsonTypeInfo">源生成的反序列化元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反序列化结果；404 或响应体为 JSON <c>null</c> 时为 null。</returns>
    private async Task<T?> GetFromGithubJsonOrNullOnNotFoundAsync<T>(string url, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
    {
        try
        {
            return await GetFromGithubJsonAsync(url, jsonTypeInfo, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            return default;
        }
    }


    /// <summary>
    /// 由一个 release 组装版本信息：按安装包文件名约定，把每个「架构 + 安装类型」的资产映射成一份明细。
    /// <para>
    /// 文件名约定来自本仓库自己的打包流程（<c>publish.cs</c> 与 <c>.github/workflows/publish_preview_3.yml</c>）：
    /// 安装版 <c>Starward_Setup_{tag}_{arch}.exe</c>、便携版 <c>Starward_Portable_{tag}_{arch}.7z</c>，
    /// 其中 <c>{arch}</c> 为 <c>x64</c> / <c>arm64</c>；键为 <c>"{arch}-{install_type}"</c> 小写。
    /// </para>
    /// </summary>
    /// <param name="release">GitHub release。</param>
    /// <returns>版本信息。</returns>
    private static ReleaseInfo CreateReleaseInfo(GithubRelease release)
    {
        var info = new ReleaseInfo
        {
            Version = NormalizeVersion(release.TagName),
            Releases = new Dictionary<string, ReleaseInfoDetail>(),
        };
        foreach (Architecture arch in new[] { Architecture.X64, Architecture.Arm64 })
        {
            string archName = arch.ToString().ToLowerInvariant();
            foreach (InstallType type in new[] { InstallType.Setup, InstallType.Portable })
            {
                GithubAsset? asset = FindPackageAsset(release, archName, type);
                if (asset is null)
                {
                    continue;
                }
                string hash = ReadSha256Digest(asset);
                var detail = new ReleaseInfoDetail
                {
                    Version = info.Version,
                    Architecture = arch,
                    InstallType = type,
                    BuildTime = release.PublishedAt,
                    // 上游发布服务可以按版本关闭自动更新，GitHub 上没有这个开关，一律允许
                    DisableAutoUpdate = false,
                    PackageUrl = asset.BrowserDownloadUrl,
                    PackageSize = asset.Size,
                    PackageHash = hash,
                    // 文件级清单（manifest）不在 GitHub release 里，只有仓库自己把清单挂成资产时才取得到
                    ManifestUrl = FindManifestAsset(release, archName) ?? string.Empty,
                    Diffs = new Dictionary<string, ReleaseInfoDiff>(),
                    // 安装版的更新方式是下载这个 Setup 可执行文件并以 update 参数运行它，所以直接复用安装包资产
                    Setup = type is InstallType.Setup
                        ? new ReleaseSetup
                        {
                            Url = asset.BrowserDownloadUrl,
                            FileName = "Starward.Setup.exe",
                            Size = asset.Size,
                            Hash = hash,
                        }
                        : null,
                };
                info.Releases[$"{arch}-{type}".ToLowerInvariant()] = detail;
            }
        }
        return info;
    }


    /// <summary>
    /// 找某个「架构 + 安装类型」的安装包资产。
    /// </summary>
    /// <param name="release">GitHub release。</param>
    /// <param name="archName">小写架构名，如 <c>x64</c>。</param>
    /// <param name="type">安装类型。</param>
    /// <returns>资产；没有时为 null。</returns>
    private static GithubAsset? FindPackageAsset(GithubRelease release, string archName, InstallType type)
    {
        string prefix = type is InstallType.Setup ? "Starward_Setup" : "Starward_Portable";
        string extension = type is InstallType.Setup ? ".exe" : ".7z";
        foreach (string version in EnumerateVersionNames(release.TagName))
        {
            GithubAsset? asset = FindAsset(release, $"{prefix}_{version}_{archName}{extension}");
            if (asset is not null)
            {
                return asset;
            }
        }
        return null;
    }


    /// <summary>
    /// 找文件级更新清单（manifest）资产，找不到时返回 null。
    /// <para>
    /// 上游把 manifest 与逐文件补丁放在自己的对象存储上，GitHub release 默认只有安装包。
    /// 这里接受几种常见的清单资产命名（<c>release_manifest_{tag}_{arch}.json</c>、
    /// <c>manifest_{tag}_{arch}.json</c>，以及不带版本号的同名变体），
    /// 仓库只要按其中任意一种把清单挂到 release 上，便携版的原地更新与安装器的按文件安装就能用；
    /// 没挂时 <see cref="ReleaseInfoDetail.ManifestUrl"/> 为空，只有「按文件增量更新 / 安装」那两条路径会失败。
    /// </para>
    /// </summary>
    /// <param name="release">GitHub release。</param>
    /// <param name="archName">小写架构名。</param>
    /// <returns>清单资产的下载地址；没有时为 null。</returns>
    private static string? FindManifestAsset(GithubRelease release, string archName)
    {
        foreach (string version in EnumerateVersionNames(release.TagName))
        {
            foreach (string name in new[] { $"release_manifest_{version}_{archName}.json", $"manifest_{version}_{archName}.json" })
            {
                GithubAsset? asset = FindAsset(release, name);
                if (asset is not null)
                {
                    return asset.BrowserDownloadUrl;
                }
            }
        }
        foreach (string name in new[] { $"release_manifest_{archName}.json", $"manifest_{archName}.json" })
        {
            GithubAsset? asset = FindAsset(release, name);
            if (asset is not null)
            {
                return asset.BrowserDownloadUrl;
            }
        }
        return null;
    }


    /// <summary>
    /// 按文件名找资产（不区分大小写）。
    /// </summary>
    /// <param name="release">GitHub release。</param>
    /// <param name="name">资产文件名。</param>
    /// <returns>资产；没有时为 null。</returns>
    private static GithubAsset? FindAsset(GithubRelease release, string name)
    {
        return release.Assets?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// 取资产的 SHA-256。GitHub 对通过 API 上传的资产会给出 <c>digest</c>（形如 <c>sha256:...</c>），
    /// 取不到时返回空串。
    /// </summary>
    /// <param name="asset">资产。</param>
    /// <returns>小写十六进制散列，或空串。</returns>
    private static string ReadSha256Digest(GithubAsset asset)
    {
        string? digest = asset.Digest;
        if (string.IsNullOrWhiteSpace(digest))
        {
            return string.Empty;
        }
        int index = digest.IndexOf(':');
        string value = index >= 0 ? digest[(index + 1)..] : digest;
        return value.Trim().ToLowerInvariant();
    }


    /// <summary>
    /// 标签可能带 <c>v</c> 前缀，去掉后再做版本比较或拼文件名。
    /// </summary>
    /// <param name="version">标签或版本号。</param>
    /// <returns>去掉前缀的版本号。</returns>
    private static string NormalizeVersion(string? version)
    {
        return (version ?? string.Empty).Trim().TrimStart('v', 'V');
    }


    /// <summary>
    /// 依次尝试的标签：原样、去掉 <c>v</c> 前缀、加上 <c>v</c> 前缀。
    /// </summary>
    /// <param name="version">版本号。</param>
    /// <returns>去重后的标签序列。</returns>
    private static IEnumerable<string> EnumerateVersionTags(string version)
    {
        string normalized = NormalizeVersion(version);
        if (!string.IsNullOrEmpty(version) && !string.Equals(version, normalized, StringComparison.Ordinal))
        {
            yield return version;
        }
        if (!string.IsNullOrEmpty(normalized))
        {
            yield return normalized;
            yield return "v" + normalized;
        }
    }


    /// <summary>
    /// 拼安装包文件名时依次尝试的版本串：原标签与去掉前缀的版本号。
    /// </summary>
    /// <param name="tagName">release 标签。</param>
    /// <returns>去重后的版本串。</returns>
    private static IEnumerable<string> EnumerateVersionNames(string? tagName)
    {
        string normalized = NormalizeVersion(tagName);
        if (!string.IsNullOrEmpty(tagName) && !string.Equals(tagName, normalized, StringComparison.Ordinal))
        {
            yield return tagName;
        }
        if (!string.IsNullOrEmpty(normalized))
        {
            yield return normalized;
        }
    }


    #endregion



    #region Github



    public async Task<GithubRelease?> GetGithubLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        string url = $"{GithubReleasesApi}?page=1&per_page=1";
        List<GithubRelease>? list = await GetFromGithubJsonAsync(url, ReleaseJsonContext.Default.ListGithubRelease, cancellationToken);
        return list?.FirstOrDefault();
    }



    public async Task<List<GithubRelease>> GetGithubReleaseAsync(int page, int perPage, CancellationToken cancellationToken = default)
    {
        string url = $"{GithubReleasesApi}?page={page}&per_page={perPage}";
        List<GithubRelease>? list = await GetFromGithubJsonAsync(url, ReleaseJsonContext.Default.ListGithubRelease, cancellationToken);
        return list ?? new List<GithubRelease>();
    }



    public async Task<GithubRelease?> GetGithubReleaseAsync(string tag, CancellationToken cancellationToken = default)
    {
        string url = $"{GithubReleasesApi}/tags/{Uri.EscapeDataString(tag)}";
        return await GetFromGithubJsonAsync(url, ReleaseJsonContext.Default.GithubRelease, cancellationToken);
    }



    public async Task<string> RenderGithubMarkdownAsync(string markdown, CancellationToken cancellationToken = default)
    {
        const string url = "https://api.github.com/markdown";
        var request = new GithubMarkdownRequest
        {
            Text = markdown,
            Mode = "gfm",
            Context = $"{GithubOwner}/{GithubRepository}",
        };
        var content = new StringContent(JsonSerializer.Serialize(request, ReleaseJsonContext.Default.GithubMarkdownRequest), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }



    #endregion


}
