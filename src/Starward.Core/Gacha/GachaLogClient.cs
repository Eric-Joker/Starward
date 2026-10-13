using Microsoft.Win32;
using Starward.Core.Gacha.Genshin;
using Starward.Core.Gacha.StarRail;
using Starward.Core.Gacha.ZZZ;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Starward.Core.Gacha;

public abstract class GachaLogClient
{


    protected const string WEB_CACHE_PATH_YS_CN = @"YuanShen_Data\webCaches\Cache\Cache_Data\data_2";
    protected const string WEB_CACHE_PATH_YS_OS = @"GenshinImpact_Data\webCaches\Cache\Cache_Data\data_2";

    protected const string API_PREFIX_YS_CN = "https://public-operation-hk4e.mihoyo.com/gacha_info/api/getGachaLog";
    protected const string API_PREFIX_YS_OS = "https://public-operation-hk4e-sg.hoyoverse.com/gacha_info/api/getGachaLog";

    protected static ReadOnlySpan<byte> SPAN_WEB_PREFIX_YS_CN => "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha"u8;
    protected static ReadOnlySpan<byte> SPAN_WEB_PREFIX_YS_OS => "https://gs.hoyoverse.com/genshin/event/e20190909gacha"u8;



    protected const string WEB_CACHE_SR_PATH = @"StarRail_Data\webCaches\Cache\Cache_Data\data_2";

    protected const string API_PREFIX_SR_CN = "https://public-operation-hkrpg.mihoyo.com/common/hkrpg_gacha_record/api/getGachaLog";
    protected const string API_PREFIX_SR_OS = "https://public-operation-hkrpg-sg.hoyoverse.com/common/hkrpg_gacha_record/api/getGachaLog";

    protected static ReadOnlySpan<byte> SPAN_WEB_PREFIX_SR_CN => "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha"u8;
    protected static ReadOnlySpan<byte> SPAN_WEB_PREFIX_SR_OS => "https://gs.hoyoverse.com/hkrpg/event/e20211215gacha"u8;


    protected const string WEB_CACHE_ZZZ_PATH = @"ZenlessZoneZero_Data\webCaches\Cache\Cache_Data\data_2";

    protected const string API_PREFIX_ZZZ_CN = "https://public-operation-nap.mihoyo.com/common/gacha_record/api/getGachaLog";
    protected const string API_PREFIX_ZZZ_OS = "https://public-operation-nap-sg.hoyoverse.com/common/gacha_record/api/getGachaLog";

    protected static ReadOnlySpan<byte> SPAN_WEB_PREFIX_ZZZ_CN => "https://webstatic.mihoyo.com/nap/event/e20230424gacha"u8;
    protected static ReadOnlySpan<byte> SPAN_WEB_PREFIX_ZZZ_OS => "https://gs.hoyoverse.com/nap/event/e20230424gacha"u8;





    protected const string REG_KEY_BH3_CN = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\崩坏3";
    protected const string REG_KEY_BH3_OS = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Honkai Impact 3";
    protected const string REG_KEY_BH3_GL = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Honkai Impact 3rd";
    protected const string REG_KEY_BH3_TW = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\崩壊3rd";
    protected const string REG_KEY_BH3_KR = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\붕괴3rd";
    protected const string REG_KEY_BH3_JP = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\崩壊3rd";




    protected readonly HttpClient _httpClient;


    public abstract IReadOnlyCollection<IGachaType> QueryGachaTypes { get; init; }



    public GachaLogClient(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
            {
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
        }
        else
        {
            _httpClient = httpClient;
        }
    }



    #region public method



    public async Task<long> GetUidByGachaUrlAsync(string gachaUrl)
    {
        var prefix = GetGachaUrlPrefix(gachaUrl);
        foreach (var gachaType in QueryGachaTypes)
        {
            var param = new GachaLogQuery(gachaType, 1, 1, 0);
            var list = await GetGachaLogByQueryAsync<GachaLogItem>(prefix, param);
            if (list.Count != 0)
            {
                return list.First().Uid;
            }
        }
        return 0;
    }



    public abstract Task<IEnumerable<GachaLogItem>> GetGachaLogAsync(string gachaUrl, long endId = 0, string? lang = null, IProgress<(IGachaType GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default);


    public abstract Task<IEnumerable<GachaLogItem>> GetGachaLogAsync(string gachaUrl, IGachaType gachaType, long endId = 0, string? lang = null, IProgress<(IGachaType GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default);


    public abstract Task<IEnumerable<GachaLogItem>> GetGachaLogAsync(string gachaUrl, GachaLogQuery query, CancellationToken cancellationToken = default);




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
    /// 从游戏 webCaches 下所有 data_2 中提取抽卡记录 URL 候选列表
    /// （文件按 mtime 新→旧，同一文件内靠后的优先）。
    /// 同时匹配活动页（webstatic / gs）与 public-operation getGachaLog API 前缀，便于命中较新的 authkey。
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
            GameBiz.hkrpg_cn or GameBiz.hkrpg_global or GameBiz.hkrpg_bilibili => @"StarRail_Data\webCaches",
            GameBiz.nap_cn or GameBiz.nap_global or GameBiz.nap_bilibili => @"ZenlessZoneZero_Data\webCaches",
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
            GameBiz.hkrpg_cn or GameBiz.hkrpg_global or GameBiz.hkrpg_bilibili => Path.Join(installPath, WEB_CACHE_SR_PATH),
            GameBiz.nap_cn or GameBiz.nap_global or GameBiz.nap_bilibili => Path.Join(installPath, WEB_CACHE_ZZZ_PATH),
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
            GameBiz.hkrpg_cn or GameBiz.hkrpg_global or GameBiz.hkrpg_bilibili => Path.Join(installPath, WEB_CACHE_SR_PATH),
            GameBiz.nap_cn or GameBiz.nap_global or GameBiz.nap_bilibili => Path.Join(installPath, WEB_CACHE_ZZZ_PATH),
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
    /// 根据 gameBiz 返回用于二进制匹配的 URL 前缀列表（活动页 + public-operation getGachaLog API）。
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
            GameBiz.hkrpg_cn or GameBiz.hkrpg_bilibili =>
            [
                SPAN_WEB_PREFIX_SR_CN.ToArray(),
                Encoding.UTF8.GetBytes(API_PREFIX_SR_CN),
            ],
            GameBiz.hkrpg_global =>
            [
                SPAN_WEB_PREFIX_SR_OS.ToArray(),
                Encoding.UTF8.GetBytes(API_PREFIX_SR_OS),
            ],
            GameBiz.nap_cn or GameBiz.nap_bilibili =>
            [
                SPAN_WEB_PREFIX_ZZZ_CN.ToArray(),
                Encoding.UTF8.GetBytes(API_PREFIX_ZZZ_CN),
            ],
            GameBiz.nap_global =>
            [
                SPAN_WEB_PREFIX_ZZZ_OS.ToArray(),
                Encoding.UTF8.GetBytes(API_PREFIX_ZZZ_OS),
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



    #endregion




    #region protected method



    protected async Task<T> CommonGetAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        var wrapper = await _httpClient.GetFromJsonAsync(url, typeof(miHoYoApiWrapper<T>), GachaLogJsonContext.Default, cancellationToken) as miHoYoApiWrapper<T>;
        if (wrapper is null)
        {
            throw new miHoYoApiException(-1, "Response body is null");
        }
        else if (wrapper.Retcode != 0)
        {
            throw new miHoYoApiException(wrapper.Retcode, wrapper.Message);
        }
        else
        {
            return wrapper.Data;
        }
    }



    protected abstract string GetGachaUrlPrefix(string gachaUrl, string? lang = null);



    protected async Task<List<T>> GetGachaLogAsync<T>(string gachaUrl, long endId = 0, string? lang = null, IProgress<(IGachaType GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default) where T : GachaLogItem
    {
        endId = Math.Clamp(endId, 0, long.MaxValue);
        var prefix = GetGachaUrlPrefix(gachaUrl, lang);
        var result = new List<T>();
        foreach (var gachaType in QueryGachaTypes)
        {
            result.AddRange(await GetGachaLogByTypeAsync<T>(prefix, gachaType, endId, progress, cancellationToken));
        }
        return result;
    }




    protected async Task<List<T>> GetGachaLogAsync<T>(string gachaUrl, IGachaType gachaType, long endId = 0, string? lang = null, IProgress<(IGachaType GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default) where T : GachaLogItem
    {
        endId = Math.Clamp(endId, 0, long.MaxValue);
        var prefix = GetGachaUrlPrefix(gachaUrl, lang);
        return await GetGachaLogByTypeAsync<T>(prefix, gachaType, endId, progress, cancellationToken);
    }




    protected virtual async Task<List<T>> GetGachaLogByQueryAsync<T>(string gachaUrlPrefix, GachaLogQuery param, CancellationToken cancellationToken = default) where T : GachaLogItem
    {
        await Task.Delay(Random.Shared.Next(200, 300), cancellationToken);
        var url = $"{gachaUrlPrefix}&{param}";
        var wrapper = await _httpClient.GetFromJsonAsync(url, typeof(miHoYoApiWrapper<GachaLogResult<T>>), GachaLogJsonContext.Default, cancellationToken) as miHoYoApiWrapper<GachaLogResult<T>>;
        if (wrapper is null)
        {
            return new List<T>();
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




    protected async Task<List<T>> GetGachaLogByTypeAsync<T>(string prefix, IGachaType gachaType, long endId = 0, IProgress<(IGachaType GachaType, int Page)>? progress = null, CancellationToken cancellationToken = default) where T : GachaLogItem
    {
        var param = new GachaLogQuery(gachaType, 1, 20, 0);
        var result = new List<T>();
        while (true)
        {
            progress?.Report((gachaType, param.Page));
            var list = await GetGachaLogByQueryAsync<T>(prefix, param, cancellationToken);
            result.AddRange(list);
            if (list.Count == 20 && list.Last().Id > endId)
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


    [SupportedOSPlatform("windows")]
    protected static string? GetGameInstallPathFromRegistry(string regKey)
    {
        var launcherPath = Registry.GetValue(regKey, "InstallPath", null) as string;
        var configPath = Path.Join(launcherPath, "config.ini");
        if (File.Exists(configPath))
        {
            var str = File.ReadAllText(configPath);
            var installPath = Regex.Match(str, @"game_install_path=(.+)").Groups[1].Value.Trim();
            if (Directory.Exists(installPath))
            {
                return Path.GetFullPath(installPath);
            }
        }
        return null;
    }


    protected static string? FindMatchStringFromFile(string path, ReadOnlySpan<byte> prefix)
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
    protected static IReadOnlyList<string> FindAllMatchStringsFromFile(string path, IReadOnlyList<byte[]> prefixes)
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


    #endregion




    #region Gacha Info


    public async Task<GenshinGachaWiki> GetGenshinGachaInfoAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default)
    {
        lang = LanguageUtil.FilterLanguage(lang);
        GenshinGachaWiki wiki;
        if (gameBiz.IsChinaServer() && lang is "zh-cn")
        {
            const string url = "https://api-takumi.mihoyo.com/event/platsimulator/config?gids=2&game=hk4e";
            wiki = await CommonGetAsync<GenshinGachaWiki>(url, cancellationToken);
        }
        else
        {
            string url = $"https://sg-public-api.hoyolab.com/event/simulatoros/config?lang={lang}";
            wiki = await CommonGetAsync<GenshinGachaWiki>(url, cancellationToken);
        }
        wiki.Language = lang;
        return wiki;
    }


    public async Task<StarRailGachaWiki> GetStarRailGachaInfoAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default)
    {
        lang = LanguageUtil.FilterLanguage(lang);
        StarRailGachaWiki wiki;
        if (gameBiz.IsChinaServer() && lang is "zh-cn")
        {
            const string url = "https://api-takumi.mihoyo.com/event/rpgsimulator/config?game=hkrpg";
            wiki = await CommonGetAsync<StarRailGachaWiki>(url, cancellationToken);
        }
        else
        {
            wiki = new StarRailGachaWiki { Game = "hkrpg", Avatar = new List<StarRailGachaInfo>(), Equipment = new List<StarRailGachaInfo>() };
            for (int i = 1; i <= 10; i++)
            {
                string url = $"https://sg-public-api.hoyolab.com/event/rpgcalc/avatar/list?game=hkrpg&lang={lang}&tab_from=TabAll&page={i}&size=100";
                var wrapper = await CommonGetAsync<StarRailGachaInfoWrapper>(url, cancellationToken);
                wiki.Avatar.AddRange(wrapper.List);
                if (wrapper.List.Count != 100)
                {
                    break;
                }
            }
            for (int i = 1; i <= 10; i++)
            {
                string url = $"https://sg-public-api.hoyolab.com/event/rpgcalc/equipment/list?game=hkrpg&lang={lang}&tab_from=TabAll&page={i}&size=100";
                var wrapper = await CommonGetAsync<StarRailGachaInfoWrapper>(url, cancellationToken);
                wiki.Equipment.AddRange(wrapper.List);
                if (wrapper.List.Count != 100)
                {
                    break;
                }
            }
        }
        wiki.Language = lang;
        return wiki;
    }


    public async Task<ZZZGachaWiki> GetZZZGachaInfoAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default)
    {
        lang = LanguageUtil.FilterLanguage(lang);
        ZZZGachaWiki wiki;
        if (gameBiz.IsChinaServer() && lang is "zh-cn")
        {
            const string url = $"https://starward-static.scighost.com/metadata/v1/zzz/ZZZGachaInfo.nap_cn.zh-cn.json";
            wiki = await CommonGetAsync<ZZZGachaWiki>(url, cancellationToken);
        }
        else
        {
            string url = $"https://starward-static.scighost.com/metadata/v1/zzz/ZZZGachaInfo.nap_global.{lang}.json";
            wiki = await CommonGetAsync<ZZZGachaWiki>(url, cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(wiki.Language))
        {
            wiki.Language = lang;
        }
        return wiki;
    }



    #endregion



}
