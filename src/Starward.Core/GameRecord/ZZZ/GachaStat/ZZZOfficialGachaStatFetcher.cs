namespace Starward.Core.GameRecord.ZZZ.GachaStat;

/// <summary>
/// 绝区零官方抽卡统计（米游社 / HoYoLAB「抽卡记录」活动页）的拉取流程：
/// 先 badge 登录换 <c>e_nap_token</c>，再逐频段拉总览、当前卡池与历史版本的 S 级记录。
/// </summary>
/// <remarks>
/// 与养成指南同一条鉴权链：数据接口只靠 Cookie，无需 DS。
/// 历史版本每次最多查 8 个（与活动页前端一致）；当前版本由 cur_pool_stat 覆盖。
/// </remarks>
internal static class ZZZOfficialGachaStatFetcher
{

    /// <summary>
    /// 拉取官方抽卡统计；<c>e_nap_token</c> 过期时重登一次。
    /// </summary>
    /// <param name="client">绝区零战绩客户端。</param>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">期望语言；国服官方名称固定中文，国际服随 Cookie 的 mi18nLang。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>总览、当前卡池与按频段分组的 S 级记录。</returns>
    /// <exception cref="miHoYoApiException">badge 登录或数据接口返回非 0。</exception>
    public static async Task<ZZZOfficialGachaStat> FetchAsync(IGameRecordZZZClient client, GameRecordRole role, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(role);
        string lang = LanguageUtil.FilterLanguage(language);
        string cookie = await client.LoginZZZCultivateBadgeAsync(role, lang, cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchByCookieAsync(client, role, cookie, cancellationToken).ConfigureAwait(false);
        }
        catch (miHoYoApiException ex) when (ex.ReturnCode is -100 or -111 or 10001)
        {
            // e_nap_token 约 48h 有效，过期时重登一次
            cookie = await client.LoginZZZCultivateBadgeAsync(role, lang, cancellationToken).ConfigureAwait(false);
            return await FetchByCookieAsync(client, role, cookie, cancellationToken).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// 用已合并 <c>e_nap_token</c> 的 Cookie 依次拉总览、各频段当前卡池与历史版本 S 级记录。
    /// </summary>
    /// <param name="client">绝区零战绩客户端。</param>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">badge 登录后合并的 Cookie。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>聚合后的官方统计。</returns>
    private static async Task<ZZZOfficialGachaStat> FetchByCookieAsync(IGameRecordZZZClient client, GameRecordRole role, string cookie, CancellationToken cancellationToken)
    {
        ZZZGachaStatIndex index = await client.GetZZZGachaStatIndexAsync(role, cookie, cancellationToken).ConfigureAwait(false);
        var result = new ZZZOfficialGachaStat
        {
            Index = index,
            // 国服官方统计的名称固定中文；国际服随请求头与 Cookie 的 mi18nLang
            Language = client is HoyolabZZZClient ? client.Language : "zh-cn",
        };
        foreach (var typeStat in index.TypeStats)
        {
            int gachaType = ZZZGachaStatType.ToGachaType(typeStat.GachaType);
            if (gachaType == 0 || typeStat.TotalCount <= 0)
            {
                continue;
            }
            var records = new Dictionary<long, ZZZGachaPoolStatItem>();
            ZZZGachaPoolStatData current = await client.GetZZZGachaCurrentPoolStatAsync(role, cookie, gachaType, cancellationToken).ConfigureAwait(false);
            result.CurrentPools[gachaType] = current.List;
            AddPoolStatRecords(records, current.List);
            string? currentVersion = current.CurrentVersion ?? index.CurrentVersion;
            var versions = typeStat.HasDataVersions
                .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, currentVersion, StringComparison.Ordinal))
                .Distinct()
                .ToList();
            foreach (var chunk in versions.Chunk(8))
            {
                ZZZGachaPoolStatData history = await client.GetZZZGachaHistoryPoolStatAsync(role, cookie, gachaType, chunk, cancellationToken).ConfigureAwait(false);
                AddPoolStatRecords(records, history.List);
            }
            result.SRecords[gachaType] = records.Values.OrderBy(x => x.Sort).ToList();
        }
        return result;
    }


    /// <summary>
    /// 把卡池里的 S 级记录按 sort 去重写入字典（当前版本上下半、历史版本之间不会重叠，去重只为防御）。
    /// </summary>
    /// <param name="records">以 sort 为键的记录表。</param>
    /// <param name="pools">卡池列表。</param>
    private static void AddPoolStatRecords(Dictionary<long, ZZZGachaPoolStatItem> records, IEnumerable<ZZZGachaPoolStat> pools)
    {
        foreach (var pool in pools)
        {
            foreach (var record in pool.Records)
            {
                if (record.Sort > 0)
                {
                    records[record.Sort] = record;
                }
            }
        }
    }

}
