using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Starward.Core.Gacha.Genshin;

public class GenshinBeyondGachaClient
{


    private const string WEB_CACHE_PATH_YS_CN = @"YuanShen_Data\webCaches\Cache\Cache_Data\data_2";
    private const string WEB_CACHE_PATH_YS_OS = @"GenshinImpact_Data\webCaches\Cache\Cache_Data\data_2";

    private static ReadOnlySpan<byte> SPAN_WEB_PREFIX_YS_CN => "https://webstatic.mihoyo.com/hk4e/event/e20250716gacha/index.html"u8;
    private static ReadOnlySpan<byte> SPAN_WEB_PREFIX_YS_OS => "https://gs.hoyoverse.com/genshin/event/e20250716gacha/index.html"u8;

    private const string API_PREFIX_YS_CN = "https://public-operation-hk4e.mihoyo.com/gacha_info/api/getBeyondGachaLog";
    private const string API_PREFIX_YS_OS = "https://public-operation-hk4e-sg.hoyoverse.com/gacha_info/api/getBeyondGachaLog";


    public IReadOnlyCollection<int> QueryGachaTypes { get; init; } = new int[] { 1000, 2000 }.ToList().AsReadOnly();



    private readonly HttpClient _httpClient;


    public GenshinBeyondGachaClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
    }





    public async Task<IEnumerable<GenshinBeyondGachaItem>> GetGachaLogAsync(string gachaUrl, long endId = 0, string? lang = null, IProgress<(int GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default)
    {
        endId = Math.Clamp(endId, 0, long.MaxValue);
        var prefix = GetGachaUrlPrefix(gachaUrl, lang);
        var result = new List<GenshinBeyondGachaItem>();
        foreach (var gachaType in QueryGachaTypes)
        {
            result.AddRange(await GetGachaLogByTypeAsync(prefix, gachaType, endId, progress, cancellationToken));
        }
        return result;
    }



    protected async Task<List<GenshinBeyondGachaItem>> GetGachaLogByTypeAsync(string prefix, int gachaType, long endId = 0, IProgress<(int GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default)
    {
        var param = new BeyondGachaLogQuery(gachaType, 1, 5, 0);
        var result = new List<GenshinBeyondGachaItem>();
        while (true)
        {
            progress?.Report((gachaType, param.Page));
            var list = await GetGachaLogByQueryAsync(prefix, param, cancellationToken);
            result.AddRange(list);
            if (list.Count == 5 && list.Last().Id > endId)
            {
                param.Page++;
                param.EndId = list.Last().Id;
            }
            else
            {
                break;
            }
        }
        return result;
    }



    protected string GetGachaUrlPrefix(string gachaUrl, string? lang = null)
    {
        var match = Regex.Match(gachaUrl, @"(https://webstatic\.mihoyo\.com[!-z]+)");
        if (match.Success)
        {
            gachaUrl = match.Groups[1].Value;
            var auth = gachaUrl.Substring(gachaUrl.IndexOf('?')).Replace("#/log", "");
            gachaUrl = API_PREFIX_YS_CN + auth;
            if (!string.IsNullOrWhiteSpace(lang))
            {
                gachaUrl = Regex.Replace(gachaUrl, @"&lang=[^&]+", $"&lang={LanguageUtil.FilterLanguage(lang)}");
            }
            else
            {
                lang = Regex.Match(gachaUrl, @"&lang=([^&]+)").Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    gachaUrl = Regex.Replace(gachaUrl, @"&lang=([^&]+)", $"&lang={LanguageUtil.FilterLanguage(lang)}");
                }
            }
            return gachaUrl;
        }
        match = Regex.Match(gachaUrl, @"(https://gs\.hoyoverse\.com[!-z]+)");
        if (match.Success)
        {
            gachaUrl = match.Groups[1].Value;
            var auth = gachaUrl.Substring(gachaUrl.IndexOf('?')).Replace("#/log", "");
            gachaUrl = API_PREFIX_YS_OS + auth;
            if (!string.IsNullOrWhiteSpace(lang))
            {
                gachaUrl = Regex.Replace(gachaUrl, @"&lang=[^&]+", $"&lang={LanguageUtil.FilterLanguage(lang)}");
            }
            else
            {
                lang = Regex.Match(gachaUrl, @"&lang=([^&]+)").Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    gachaUrl = Regex.Replace(gachaUrl, @"&lang=([^&]+)", $"&lang={LanguageUtil.FilterLanguage(lang)}");
                }
            }
            return gachaUrl;
        }
        match = Regex.Match(gachaUrl, @"(https://public-operation-hk4e[!-z]+)");
        if (match.Success)
        {
            gachaUrl = match.Groups[1].Value;
            // 清理分页与类型参数；\d+ 才能完整匹配多位数（如 gacha_type=1000、size=5）
            gachaUrl = Regex.Replace(gachaUrl, @"&gacha_type=\d+", "");
            gachaUrl = Regex.Replace(gachaUrl, @"&page=\d+", "");
            gachaUrl = Regex.Replace(gachaUrl, @"&size=\d+", "");
            gachaUrl = Regex.Replace(gachaUrl, @"&end_id=\d+", "");
            if (!string.IsNullOrWhiteSpace(lang))
            {
                gachaUrl = Regex.Replace(gachaUrl, @"&lang=[^&]+", $"&lang={LanguageUtil.FilterLanguage(lang)}");
            }
            return gachaUrl;
        }
        throw new ArgumentException(CoreLang.Gacha_CannotParseTheWishRecordURL);
    }



    protected virtual async Task<List<GenshinBeyondGachaItem>> GetGachaLogByQueryAsync(string gachaUrlPrefix, BeyondGachaLogQuery param, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Random.Shared.Next(200, 300), cancellationToken);
        var url = $"{gachaUrlPrefix}&{param}";
        var wrapper = await _httpClient.GetFromJsonAsync(url, typeof(miHoYoApiWrapper<GenshinBeyondGachaResult>), GachaLogJsonContext.Default, cancellationToken) as miHoYoApiWrapper<GenshinBeyondGachaResult>;
        if (wrapper is null)
        {
            return new List<GenshinBeyondGachaItem>();
        }
        else if (wrapper.Retcode != 0)
        {
            throw new GachaApiException(wrapper.Retcode, wrapper.Message);
        }
        else
        {
            return wrapper.Data.List;
        }
    }




    public static string? GetGachaUrlFromWebCache(GameBiz gameBiz, string? installPath = null)
    {
        var candidates = GetGachaUrlCandidatesFromWebCache(gameBiz, installPath);
        return candidates.Count > 0 ? candidates[0] : null;
    }



    /// <summary>
    /// 从网页缓存提取 URL 时保留的最大候选数（限制校验时的 API 请求次数）。
    /// </summary>
    public const int MaxGachaUrlCandidates = 10;



    /// <summary>
    /// 从游戏 webCaches 下所有 data_2 中提取千星奇域抽卡记录 URL 候选列表
    /// （文件按 mtime 新→旧，同一文件内靠后的优先）。
    /// 同时匹配活动页（webstatic / gs）与 public-operation getBeyondGachaLog API 前缀。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <param name="installPath">游戏安装根目录。</param>
    /// <param name="maxCount">最多返回的候选数，默认 <see cref="MaxGachaUrlCandidates"/>。</param>
    /// <returns>按 authkey 去重后的 URL 列表；无匹配时为空列表。</returns>
    public static IReadOnlyList<string> GetGachaUrlCandidatesFromWebCache(GameBiz gameBiz, string? installPath = null, int maxCount = MaxGachaUrlCandidates)
    {
        maxCount = Math.Clamp(maxCount, 1, 50);
        var patterns = GetGachaUrlPatterns(gameBiz);
        var result = new List<string>(maxCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in GetAllGachaCacheFilePaths(gameBiz, installPath))
        {
            if (!File.Exists(file))
            {
                continue;
            }
            foreach (var url in FindAllMatchStringsFromFile(file, patterns))
            {
                // 同一 authkey 可能以活动页与 API 两种形态出现，用 authkey 去重
                string dedupeKey = ExtractAuthKeyDedupeKey(url);
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }
                result.Add(url);
                if (result.Count >= maxCount)
                {
                    return result;
                }
            }
        }
        return result;
    }



    public static string GetWebCachesFolderPath(GameBiz gameBiz, string? installPath)
    {
        string prefix = gameBiz.Value switch
        {
            GameBiz.hk4e_cn or GameBiz.hk4e_bilibili => @"YuanShen_Data\webCaches",
            GameBiz.hk4e_global => @"GenshinImpact_Data\webCaches",
            _ => throw new ArgumentOutOfRangeException($"Unknown region {gameBiz}"),
        };
        return Path.Join(installPath, prefix);
    }



    public static string GetGachaCacheFilePath(GameBiz gameBiz, string? installPath)
    {
        var files = GetAllGachaCacheFilePaths(gameBiz, installPath);
        if (files.Count > 0)
        {
            return files[0];
        }
        // 无任何 data_2 时仍返回默认相对路径，供 IsGachaCacheFileExists 等检查
        return gameBiz.Value switch
        {
            GameBiz.hk4e_cn or GameBiz.hk4e_bilibili => Path.Join(installPath, WEB_CACHE_PATH_YS_CN),
            GameBiz.hk4e_global => Path.Join(installPath, WEB_CACHE_PATH_YS_OS),
            _ => throw new ArgumentOutOfRangeException($"Unknown region {gameBiz}"),
        };
    }



    /// <summary>
    /// 枚举游戏 webCaches 中所有存在的 data_2 缓存文件路径，按最后写入时间从新到旧排序。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <param name="installPath">游戏安装根目录。</param>
    /// <returns>存在的 data_2 完整路径列表（新→旧）；无文件时为空列表。</returns>
    public static IReadOnlyList<string> GetAllGachaCacheFilePaths(GameBiz gameBiz, string? installPath)
    {
        var candidates = new List<(string Path, DateTime Mtime)>();
        string defaultFile = gameBiz.Value switch
        {
            GameBiz.hk4e_cn or GameBiz.hk4e_bilibili => Path.Join(installPath, WEB_CACHE_PATH_YS_CN),
            GameBiz.hk4e_global => Path.Join(installPath, WEB_CACHE_PATH_YS_OS),
            _ => throw new ArgumentOutOfRangeException($"Unknown region {gameBiz}"),
        };
        if (File.Exists(defaultFile))
        {
            candidates.Add((defaultFile, File.GetLastWriteTime(defaultFile)));
        }
        string webCache = GetWebCachesFolderPath(gameBiz, installPath);
        if (Directory.Exists(webCache))
        {
            foreach (var item in Directory.GetDirectories(webCache))
            {
                string target = Path.Join(item, @"Cache\Cache_Data\data_2");
                if (File.Exists(target))
                {
                    // defaultFile 与某个子目录可能指向同一路径，避免重复
                    if (candidates.Any(c => string.Equals(c.Path, target, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    candidates.Add((target, File.GetLastWriteTime(target)));
                }
            }
        }
        return candidates
            .OrderByDescending(c => c.Mtime)
            .Select(c => c.Path)
            .ToList();
    }



    /// <summary>
    /// 根据 gameBiz 返回用于二进制匹配的 URL 前缀列表
    /// （活动页 <c>.../e20250716gacha/index.html</c> + public-operation getBeyondGachaLog API）。
    /// </summary>
    private static IReadOnlyList<byte[]> GetGachaUrlPatterns(GameBiz gameBiz)
    {
        return gameBiz.Value switch
        {
            GameBiz.hk4e_cn or GameBiz.hk4e_bilibili =>
            [
                SPAN_WEB_PREFIX_YS_CN.ToArray(),
                Encoding.UTF8.GetBytes(API_PREFIX_YS_CN),
            ],
            GameBiz.hk4e_global =>
            [
                SPAN_WEB_PREFIX_YS_OS.ToArray(),
                Encoding.UTF8.GetBytes(API_PREFIX_YS_OS),
            ],
            _ => throw new ArgumentOutOfRangeException($"Unknown region {gameBiz}"),
        };
    }



    /// <summary>
    /// 从 URL 中提取用于去重的键：优先 authkey 参数，否则用完整 URL。
    /// </summary>
    private static string ExtractAuthKeyDedupeKey(string url)
    {
        var match = Regex.Match(url, @"[?&]authkey=([^&#]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        return url;
    }



    private static string? FindMatchStringFromFile(string path, ReadOnlySpan<byte> prefix)
    {
        var all = FindAllMatchStringsFromFile(path, [prefix.ToArray()]);
        return all.Count > 0 ? all[0] : null;
    }



    /// <summary>
    /// 在指定二进制缓存文件中，对多个前缀从后向前提取全部完整 URL（文件内靠后优先）。
    /// 每个文件只读取一次；URL 以 \0 或 " 作为结束。
    /// </summary>
    /// <param name="path">缓存文件路径（通常是 data_2）。</param>
    /// <param name="prefixes">要匹配的 URL 前缀字节序列列表。</param>
    /// <returns>按文件内出现位置从后向前排序的 URL 列表（同一文件内不去重）。</returns>
    private static IReadOnlyList<string> FindAllMatchStringsFromFile(string path, IReadOnlyList<byte[]> prefixes)
    {
        if (prefixes.Count == 0)
        {
            return [];
        }
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        var hits = new List<(int Index, string Url)>();
        foreach (var prefix in prefixes)
        {
            if (prefix.Length == 0)
            {
                continue;
            }
            // 从后向前反复 LastIndexOf，每次把搜索终点前移到当前命中点
            int searchEnd = bytes.Length;
            while (searchEnd >= prefix.Length)
            {
                int index = bytes.AsSpan(0, searchEnd).LastIndexOf(prefix);
                if (index < 0)
                {
                    break;
                }
                var rest = bytes.AsSpan(index);
                int endRel = rest.IndexOfAny("\0\""u8);
                if (endRel < 0)
                {
                    endRel = rest.Length;
                }
                // 结束符紧贴前缀说明不是合法 URL，跳过
                if (endRel > prefix.Length)
                {
                    string url = Encoding.UTF8.GetString(bytes, index, endRel);
                    hits.Add((index, url));
                }
                searchEnd = index;
            }
        }
        // 文件内靠后的优先（通常更新）
        return hits
            .OrderByDescending(h => h.Index)
            .Select(h => h.Url)
            .ToList();
    }



    public async Task<long> GetUidByGachaUrlAsync(string gachaUrl)
    {
        var prefix = GetGachaUrlPrefix(gachaUrl);
        foreach (var gachaType in QueryGachaTypes)
        {
            var param = new BeyondGachaLogQuery(gachaType, 1, 1, 0);
            var list = await GetGachaLogByQueryAsync(prefix, param);
            if (list.Count != 0)
            {
                return list.First().Uid;
            }
        }
        return 0;
    }



    public async Task<List<GenshinBeyondGachaInfo>> GetGenshinBeyondGachaInfoAsync(CancellationToken cancellationToken = default)
    {
        const string url = "https://starward-static.scighost.com/game-assets/genshin/GenshinBeyondGachaInfo.json";
        var result = await _httpClient.GetFromJsonAsync(url, typeof(List<GenshinBeyondGachaInfo>), GachaLogJsonContext.Default, cancellationToken) as List<GenshinBeyondGachaInfo>;
        return result ?? [];
    }



}
