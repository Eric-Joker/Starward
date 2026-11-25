using Dapper;
using Microsoft.Extensions.Logging;
using MiniExcelLibs;
using Starward.Core;
using Starward.Core.Gacha;
using Starward.Core.Gacha.ZZZ;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.ZZZ.GachaStat;
using Starward.Features.Database;
using Starward.Features.Gacha.UIGF;
using Starward.Features.GameRecord.ZZZ;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.Gacha;

internal class ZZZGachaService : GachaLogService
{


    protected override GameBiz CurrentGameBiz { get; } = GameBiz.nap;

    protected override string GachaTableName { get; } = "ZZZGachaItem";


    private readonly GameRecordZZZService _gameRecordService;



    public ZZZGachaService(ILogger<ZZZGachaService> logger, ZZZGachaClient client, GameRecordZZZService gameRecordService) : base(logger, client)
    {
        _gameRecordService = gameRecordService;
    }


    protected override List<GachaLogItemEx> GetGachaLogItemsByQueryType(IEnumerable<GachaLogItemEx> items, IGachaType type)
    {
        return type switch
        {
            _ => items.Where(x => x.GachaType == type.Value).ToList(),
        };
    }



    public override List<GachaLogItemEx> GetGachaLogItemEx(long uid)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GachaLogItemEx>("""
            SELECT item.*, info.Icon FROM ZZZGachaItem item LEFT JOIN ZZZGachaInfo info ON item.ItemId=Info.Id WHERE Uid=@uid ORDER BY item.Id;
            """, new { uid }).ToList();
        foreach (var type in QueryGachaTypes)
        {
            var l = GetGachaLogItemsByQueryType(list, type);
            int index = 0;
            int pity = 0;
            bool hasNoUp = GachaNoUp.Dictionary.TryGetValue($"{CurrentGameBiz}{type.Value}", out var noUp);
            foreach (var item in l)
            {
                item.Index = ++index;
                item.Pity = ++pity;
                if (item.RankType == 4)
                {
                    pity = 0;
                    item.HasUpItem = hasNoUp;
                    if (hasNoUp)
                    {
                        bool isUp = true;
                        if (noUp!.Items.TryGetValue(item.ItemId, out GachaNoUpItem? noUpItem))
                        {
                            foreach ((DateTime start, DateTime end) in noUpItem.NoUpTimes)
                            {
                                if (item.Time >= start && item.Time <= end)
                                {
                                    isUp = false;
                                    break;
                                }
                            }
                        }
                        item.IsUp = isUp;
                    }
                }
            }
        }
        return list;
    }



    public override async Task<long> GetGachaLogAsync(string url, bool all, string? lang = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var dapper = DatabaseService.CreateConnection();
        // 正在获取 uid
        progress?.Report(Lang.GachaLogService_GettingUid);
        var uid = await _client.GetUidByGachaUrlAsync(url);
        if (uid == 0)
        {
            // 该账号最近6个月没有抽卡记录
            progress?.Report(Lang.GachaLogService_ThisAccountHasNoGachaRecordsInTheLast6Months);
        }
        else
        {
            long endId = 0;
            if (!all)
            {
                endId = dapper.QueryFirstOrDefault<long>($"SELECT Id FROM {GachaTableName} WHERE Uid = @Uid ORDER BY Id DESC LIMIT 1;", new { Uid = uid });
                _logger.LogInformation($"Last gacha log id of uid {uid} is {endId}");
            }

            var internalProgress = new Progress<(IGachaType GachaType, int Page)>((x) => progress?.Report(string.Format(Lang.GachaLogService_GetGachaProgressText, x.GachaType.ToLocalization(), x.Page)));
            var list = (await _client.GetGachaLogAsync(url, endId, lang, internalProgress, cancellationToken)).ToList();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException();
            }
            var oldCount = dapper.QueryFirstOrDefault<int>($"SELECT COUNT(*) FROM {GachaTableName} WHERE Uid = @Uid;", new { Uid = uid });
            InsertGachaLogItems(list);
            var newCount = dapper.QueryFirstOrDefault<int>($"SELECT COUNT(*) FROM {GachaTableName} WHERE Uid = @Uid;", new { Uid = uid });
            // 获取 {list.Count} 条记录，新增 {newCount - oldCount} 条记录
            progress?.Report(string.Format(Lang.GachaLogService_GetGachaResult, list.Count, newCount - oldCount));
        }
        return uid;
    }


    public async Task<long> GetGachaLogByGameRecordAsync(GameRecordRole role, bool all, string? lang = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (role is null)
        {
            throw new ArgumentNullException(nameof(role));
        }
        using var dapper = DatabaseService.CreateConnection();
        // 正在获取 uid
        progress?.Report(Lang.GachaLogService_GettingUid);
        long uid = role.Uid;
        if (uid == 0)
        {
            // 该账号最近6个月没有抽卡记录
            progress?.Report(Lang.GachaLogService_ThisAccountHasNoGachaRecordsInTheLast6Months);
            return 0;
        }

        // requestLanguage: global 场景下，用于更新 HoYoLAB 请求头语言。
        // recordLanguage: 用于本地落库语言标记与 item_type 映射规则（国服固定 zh-cn）。
        bool isGlobal = role.GameBiz?.EndsWith("_global", StringComparison.OrdinalIgnoreCase) ?? false;
        string recordLanguage = "zh-cn";
        string? requestLanguage = null;
        if (isGlobal)
        {
            string sourceLanguage = string.IsNullOrWhiteSpace(lang)
                ? System.Globalization.CultureInfo.CurrentUICulture.Name
                : lang;
            string normalizedLanguage = LanguageUtil.FilterLanguage(sourceLanguage);

            requestLanguage = normalizedLanguage;
            recordLanguage = normalizedLanguage;
        }

        long endId = 0;
        if (!all)
        {
            endId = dapper.QueryFirstOrDefault<long>($"SELECT Id FROM {GachaTableName} WHERE Uid = @Uid ORDER BY Id DESC LIMIT 1;", new { Uid = uid });
            _logger.LogInformation("Last gacha log id of uid {Uid} is {EndId}", uid, endId);
        }

        var list = new List<GachaLogItem>();
        foreach (IGachaType queryType in QueryGachaTypes)
        {
            ZZZGachaType gachaType = queryType.Value;
            bool stop = false;
            long? queryEndId = null;
            int page = 1;
            while (!stop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(string.Format(Lang.GachaLogService_GetGachaProgressText, queryType.ToLocalization(), page));
                var data = await _gameRecordService.GetZZZGachaRecordAsync(role, gachaType, queryEndId, requestLanguage, cancellationToken);
                var pageItems = data.GachaItemList ?? new List<ZZZGachaRecordItem>();
                if (pageItems.Count == 0)
                {
                    break;
                }
                foreach (var item in pageItems)
                {
                    if (!all && endId > 0 && item.Id <= endId)
                    {
                        stop = true;
                        break;
                    }
                    list.Add(ToGachaLogItem(uid, gachaType, item, recordLanguage));
                }
                if (stop || !data.HasMore)
                {
                    break;
                }
                long nextEndId = pageItems.Last().Id;
                if (nextEndId <= 0 || nextEndId == queryEndId.GetValueOrDefault())
                {
                    _logger.LogWarning("ZZZ gacha_record paging is not advancing (Uid: {Uid}, GachaType: {GachaType}, EndId: {EndId}). Stop paging to avoid infinite loop.", uid, gachaType, nextEndId);
                    break;
                }
                queryEndId = nextEndId;
                page++;
            }
        }

        var oldCount = dapper.QueryFirstOrDefault<int>($"SELECT COUNT(*) FROM {GachaTableName} WHERE Uid = @Uid;", new { Uid = uid });
        InsertGachaLogItems(list);
        var newCount = dapper.QueryFirstOrDefault<int>($"SELECT COUNT(*) FROM {GachaTableName} WHERE Uid = @Uid;", new { Uid = uid });
        // 获取 {list.Count} 条记录，新增 {newCount - oldCount} 条记录
        progress?.Report(string.Format(Lang.GachaLogService_GetGachaResult, list.Count, newCount - oldCount));
        return uid;
    }


    /// <summary>
    /// 补全记录 Id 判定条件（SQL）：Count 为 0 且 Id 不是 100 的整数倍，见 <see cref="ZZZGachaGapFiller.IsFillRecord"/>。
    /// </summary>
    internal const string FillRecordSqlCondition = "(Count = 0 AND Id % 100 != 0)";


    /// <summary>
    /// 用官方抽卡统计（米游社 / HoYoLAB 抽卡记录活动页）补全本地缺失的调频记录。
    /// </summary>
    /// <remarks>
    /// 先做一次增量同步拉齐近期记录，剩下的差额就是超出记录接口保留期的老记录。
    /// S 级按官方物品与时间写入，A/B 级只写占位；逐频段计算，对不上的频段跳过且保留原有补全记录。
    /// 重复执行会先删掉该频段旧的补全记录再写入，结果不变。
    /// </remarks>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="lang">软件 UI 语言，用于同步与名称回写。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补全结果摘要。</returns>
    public async Task<ZZZGachaFillSummary> FillMissingFromOfficialStatsAsync(GameRecordRole role, string? lang = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        long uid = role.Uid;
        string displayLanguage = LanguageUtil.FilterLanguage(
            string.IsNullOrWhiteSpace(lang)
                ? System.Globalization.CultureInfo.CurrentUICulture.Name
                : lang);

        // 补全只针对超出保留期的老记录，近期记录必须先同步齐，否则会把没同步的新记录当成缺口
        await GetGachaLogByGameRecordAsync(role, false, displayLanguage, progress, cancellationToken);

        progress?.Report(Lang.ZZZGachaService_GettingOfficialGachaStats);
        ZZZOfficialGachaStat stat = await _gameRecordService.GetZZZOfficialGachaStatAsync(role, displayLanguage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var dapper = DatabaseService.CreateConnection();
        // 读本地记录、计算、写入放在同一个写事务里：期间若有导入等操作插入老记录，会排队等这里提交，不会让补全基于过期的本地数据
        using var t = dapper.BeginTransaction();
        var all = dapper.Query<ZZZGachaItem>($"SELECT * FROM {GachaTableName} WHERE Uid = @uid ORDER BY Id;", new { uid }, t).ToList();
        long minRealId = all.Where(x => !ZZZGachaGapFiller.IsFillRecord(x.Id, x.Count)).Select(x => x.Id).DefaultIfEmpty(100_000_000_000_000_000).Min();
        TimeSpan fallbackOffset = GetServerUtcOffset(role.Region);
        var itemTypeById = dapper.Query<(long ItemId, string ItemType)>(
            $"SELECT ItemId, ItemType FROM {GachaTableName} WHERE ItemId != 0 AND Count != 0 GROUP BY ItemId;", transaction: t)
            .ToDictionary(x => x.ItemId, x => x.ItemType);

        var lines = new List<string>();
        bool hasWarning = false;
        var clearTypes = new List<int>();
        var newItems = new List<ZZZGachaItem>();
        foreach (IGachaType queryType in QueryGachaTypes)
        {
            int gachaType = queryType.Value;
            int idSuffix = GetFillIdSuffix(gachaType);
            ZZZGachaTypeStat? typeStat = stat.Index.TypeStats.FirstOrDefault(x => ZZZGachaStatType.ToGachaType(x.GachaType) == gachaType);
            if (idSuffix == 0 || typeStat is null)
            {
                continue;
            }
            var localReal = all.Where(x => x.GachaType == gachaType && !ZZZGachaGapFiller.IsFillRecord(x.Id, x.Count))
                               .Select(x => new ZZZGapLocalRecord(x.Id, x.ItemId, x.RankType, x.Time))
                               .ToList();
            var officialRecords = stat.SRecords.GetValueOrDefault(gachaType) ?? [];
            var officialS = officialRecords.Select(x => new ZZZGapOfficialS(x.Sort, x.ItemId, x.RecordTime, x.Count)).ToList();
            var occupied = all.Where(x => !(x.GachaType == gachaType && ZZZGachaGapFiller.IsFillRecord(x.Id, x.Count))).Select(x => x.Id).ToHashSet();
            ZZZGapFillResult result = ZZZGachaGapFiller.Compute(localReal, officialS, typeStat.TotalCount, fallbackOffset, idSuffix, minRealId, occupied, DateTime.UtcNow);
            string typeName = queryType.ToLocalization();
            switch (result.Status)
            {
                case ZZZGapFillStatus.Filled:
                    clearTypes.Add(gachaType);
                    var officialBySort = officialRecords.GroupBy(x => x.Sort).ToDictionary(x => x.Key, x => x.First());
                    foreach (var record in result.Records)
                    {
                        newItems.Add(ToFillItem(uid, gachaType, record, officialBySort, itemTypeById, stat.Language));
                    }
                    lines.Add(string.Format(Lang.ZZZGachaService_FillResult, typeName, result.MissingCount, result.SRankCount));
                    _logger.LogInformation("ZZZ gacha fill: uid {Uid}, type {GachaType}, filled {Count} (S {SCount})", uid, gachaType, result.MissingCount, result.SRankCount);
                    break;
                case ZZZGapFillStatus.NothingMissing:
                    // 本地真实记录已齐（例如后来导入了老记录），旧的补全记录多余
                    clearTypes.Add(gachaType);
                    break;
                case ZZZGapFillStatus.LocalBehind:
                    hasWarning = true;
                    lines.Add(string.Format(Lang.ZZZGachaService_FillLocalBehind, typeName));
                    _logger.LogWarning("ZZZ gacha fill local behind: uid {Uid}, type {GachaType}, {Reason}", uid, gachaType, result.Reason);
                    break;
                case ZZZGapFillStatus.OfficialBehind:
                    hasWarning = true;
                    lines.Add(string.Format(Lang.ZZZGachaService_FillOfficialBehind, typeName));
                    _logger.LogWarning("ZZZ gacha fill official stats behind: uid {Uid}, type {GachaType}, {Reason}", uid, gachaType, result.Reason);
                    break;
                default:
                    hasWarning = true;
                    lines.Add(string.Format(Lang.ZZZGachaService_FillSkipped, typeName));
                    _logger.LogWarning("ZZZ gacha fill skipped: uid {Uid}, type {GachaType}, status {Status}, {Reason}", uid, gachaType, result.Status, result.Reason);
                    break;
            }
        }

        if (clearTypes.Count > 0)
        {
            dapper.Execute($"DELETE FROM {GachaTableName} WHERE Uid = @uid AND GachaType IN @clearTypes AND {FillRecordSqlCondition};", new { uid, clearTypes }, t);
            dapper.Execute("""
                INSERT OR REPLACE INTO ZZZGachaItem (Uid, Id, Name, Time, ItemId, ItemType, RankType, GachaType, Count, Lang)
                VALUES (@Uid, @Id, @Name, @Time, @ItemId, @ItemType, @RankType, @GachaType, @Count, @Lang);
                """, newItems, t);
        }
        t.Commit();
        if (newItems.Exists(x => x.ItemId != 0))
        {
            // 官方名称是国服中文 / 国际服请求语言，按软件语言回写；名称刷新失败不影响已写入的补全记录
            try
            {
                await ChangeGachaItemNameAsync(role.GameBiz, displayLanguage, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to update ZZZ gacha item names after filling records");
            }
        }
        if (lines.Count == 0)
        {
            lines.Add(Lang.ZZZGachaService_FillNothingMissing);
        }
        return new ZZZGachaFillSummary(uid, string.Join(Environment.NewLine, lines), hasWarning);
    }


    /// <summary>
    /// 把补全计算结果转成可落库的记录：S 级带官方物品与名称，占位物品 Id 为 0、稀有度为 0。
    /// </summary>
    /// <param name="uid">玩家 UID。</param>
    /// <param name="gachaType">频段。</param>
    /// <param name="record">补全记录。</param>
    /// <param name="officialBySort">官方 S 级（按 sort 索引），S 级的 Position 即 sort。</param>
    /// <param name="itemTypeById">本地已有记录的物品类型文本，优先沿用以保持一致。</param>
    /// <param name="language">官方名称所用语言。</param>
    /// <returns>Count 为 0 的补全记录。</returns>
    private static ZZZGachaItem ToFillItem(long uid, int gachaType, ZZZGapFillRecord record, Dictionary<long, ZZZGachaPoolStatItem> officialBySort, Dictionary<long, string> itemTypeById, string language)
    {
        var item = new ZZZGachaItem
        {
            Uid = uid,
            Id = record.Id,
            GachaType = gachaType,
            Time = record.Time,
            Count = 0,
            Lang = language,
            Name = "",
            ItemType = "",
        };
        if (record.IsSRank && officialBySort.TryGetValue(record.Position, out var official))
        {
            bool isChinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            item.ItemId = official.ItemId;
            item.Name = official.Name;
            item.RankType = 4;
            item.ItemType = itemTypeById.GetValueOrDefault(official.ItemId) ?? official.ItemType switch
            {
                "ItemType_Avatar" => isChinese ? "代理人" : "ITEM_TYPE_AVATAR",
                "ItemType_Weapon" => isChinese ? "音擎" : "ITEM_TYPE_WEAPON",
                "ItemType_Bangboo" => isChinese ? "邦布" : "ITEM_TYPE_BANGBOO",
                _ => official.ItemType,
            };
        }
        return item;
    }


    /// <summary>
    /// 各频段补全记录 Id 的后两位，互不相同，保证不同频段的补全记录不会撞 Id。
    /// </summary>
    /// <param name="gachaType">频段。</param>
    /// <returns>1~99；未知频段返回 0（不补全）。</returns>
    private static int GetFillIdSuffix(int gachaType) => gachaType switch
    {
        ZZZGachaType.StandardChannel => 11,
        ZZZGachaType.ExclusiveChannel => 12,
        ZZZGachaType.WEngineChannel => 13,
        ZZZGachaType.BangbooChannel => 15,
        ZZZGachaType.ExclusiveRescreening => 42,
        ZZZGachaType.WEngineReverberation => 43,
        _ => 0,
    };


    /// <summary>
    /// 区服的服务器时区，仅在无法从本地 S 级反推时区时使用。
    /// </summary>
    /// <param name="region">区服，如 prod_gf_cn。</param>
    /// <returns>相对 UTC 的偏移。</returns>
    private static TimeSpan GetServerUtcOffset(string? region) => region switch
    {
        "prod_gf_eu" => TimeSpan.FromHours(1),
        "prod_gf_us" or "prod_gf_usa" => TimeSpan.FromHours(-5),
        _ => TimeSpan.FromHours(8),
    };


    public override (List<GachaTypeStats> GachaStats, List<GachaLogItemEx> ItemStats) GetGachaTypeStats(long uid)
    {
        var statsList = new List<GachaTypeStats>();
        var groupStats = new List<GachaLogItemEx>();
        using var dapper = DatabaseService.CreateConnection();
        var allItems = GetGachaLogItemEx(uid);
        if (allItems.Count > 0)
        {
            foreach (IGachaType type in QueryGachaTypes)
            {
                var list = GetGachaLogItemsByQueryType(allItems, type);
                if (list.Count == 0)
                {
                    continue;
                }
                var stats = new GachaTypeStats
                {
                    GachaType = type.Value,
                    GachaTypeText = type.ToLocalization(),
                    Count = list.Count,
                    Count_5_Up = list.Count(x => x.RankType == 4 && x.IsUp),
                    Count_5 = list.Count(x => x.RankType == 4),
                    Count_4 = list.Count(x => x.RankType == 3),
                    Count_3 = list.Count(x => x.RankType == 2),
                    StartTime = list.First().Time,
                    EndTime = list.Last().Time,
                    Pity_5_Max = GetPityLimit(type),
                    ShowPityProgress = ShouldShowPityProgress(type, list.Count),
                };
                stats.Ratio_5 = (double)stats.Count_5 / stats.Count;
                // A/B 级出率不计占位：补全只知道缺了几抽，不知道其中 A/B 各几个
                int knownCount = list.Count(x => !ZZZGachaGapFiller.IsPlaceholder(x.Id, x.Count, x.RankType));
                stats.Ratio_4 = knownCount > 0 ? (double)stats.Count_4 / knownCount : 0;
                stats.Ratio_3 = knownCount > 0 ? (double)stats.Count_3 / knownCount : 0;
                stats.List_5 = list.Where(x => x.RankType == 4).Reverse().ToList();
                stats.List_4 = list.Where(x => x.RankType == 3).Reverse().ToList();
                stats.Pity_5 = list.Last().Pity;
                if (list.Last().RankType == 4)
                {
                    stats.Pity_5 = 0;
                }
                // 无 S 级样本时不计算平均，避免 NaN；展示侧用 Average_5_Text 显示「—」
                if (stats.Count_5 > 0)
                {
                    stats.Average_5 = (double)(stats.Count - stats.Pity_5) / stats.Count_5;
                }
                stats.Pity_4 = list.Count - 1 - list.FindLastIndex(x => x.RankType == 3);
                // A 级平均抽数：与千星奇域页同一公式 (Count - Pity_4) / Count_4；无样本时不计算，展示侧显示「—」
                if (stats.Count_4 > 0)
                {
                    stats.Average_4 = (double)(stats.Count - stats.Pity_4) / stats.Count_4;
                }

                if (stats.Count_5_Up > 0)
                {
                    int c = stats.Count - stats.Pity_5;
                    stats.Average_5_Up = (double)c / stats.Count_5_Up;
                }

                // 「不歪概率」：仅 UP（限定）频段统计小保底 50/50 的不歪情况（ZZZ 中 S 级为 RankType==4）。
                stats.HasUpItem = GachaNoUp.Dictionary.ContainsKey($"{CurrentGameBiz}{type.Value}");
                if (stats.HasUpItem)
                {
                    (stats.FiftyFiftyCount, stats.FiftyFiftyNoUpCount, stats.MaxFiftyFiftyUpStreak, stats.MaxFiftyFiftyMissStreak, stats.IsNextPityGuaranteed) = CountFiftyFiftyNoUp(list, 4);
                }

                int pity_4 = 0;
                foreach (var item in list)
                {
                    pity_4++;
                    if (item.RankType == 3)
                    {
                        item.Pity = pity_4;
                        pity_4 = 0;
                    }
                }

                statsList.Add(stats);
            }
            groupStats = allItems.Where(x => !ZZZGachaGapFiller.IsPlaceholder(x.Id, x.Count, x.RankType))
                                 .GroupBy(x => x.ItemId)
                                 .Select(x => { var item = x.First(); item.ItemCount = x.Count(); return item; })
                                 .OrderByDescending(x => x.RankType)
                                 .ThenByDescending(x => x.ItemCount)
                                 .ThenByDescending(x => x.Time)
                                 .ToList();
        }
        return (statsList, groupStats);
    }




    protected override int InsertGachaLogItems(List<GachaLogItem> items)
    {
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        int removed = RemoveFillRecordsInvalidatedBy(dapper, t, items);
        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} ZZZ gacha fill records because older real records were inserted", removed);
        }
        var affect = dapper.Execute("""
            INSERT OR REPLACE INTO ZZZGachaItem (Uid, Id, Name, Time, ItemId, ItemType, RankType, GachaType, Count, Lang)
            VALUES (@Uid, @Id, @Name, @Time, @ItemId, @ItemType, @RankType, @GachaType, @Count, @Lang);
            """, items, t);
        t.Commit();
        return affect;
    }


    /// <summary>
    /// 写入真实记录前：若有新记录插进了某频段已有历史的中间或前面（导入老记录、隔久了重新同步），
    /// 该频段的补全记录可能与真实记录重复计数，整频段删掉，需要时让用户重新补全。只在末尾追加的增量同步不受影响。
    /// </summary>
    /// <remarks>所有往 ZZZGachaItem 写真实记录的路径（同步、UIGF v3/v4 导入）都要在同一事务里先调用。</remarks>
    /// <param name="dapper">数据库连接。</param>
    /// <param name="transaction">与写入共用的事务。</param>
    /// <param name="items">即将写入的真实记录。</param>
    /// <returns>删除的补全记录条数。</returns>
    internal static int RemoveFillRecordsInvalidatedBy(System.Data.IDbConnection dapper, System.Data.IDbTransaction transaction, IEnumerable<GachaLogItem> items)
    {
        int total = 0;
        foreach (var group in items.GroupBy(x => (x.Uid, x.GachaType)))
        {
            var existing = dapper.Query<(long Id, int Count)>(
                "SELECT Id, Count FROM ZZZGachaItem WHERE Uid = @Uid AND GachaType = @GachaType;",
                new { group.Key.Uid, group.Key.GachaType }, transaction).ToList();
            if (!existing.Exists(x => ZZZGachaGapFiller.IsFillRecord(x.Id, x.Count)))
            {
                continue;
            }
            var existingIds = existing.Select(x => x.Id).ToHashSet();
            long maxRealId = existing.Where(x => !ZZZGachaGapFiller.IsFillRecord(x.Id, x.Count)).Select(x => x.Id).DefaultIfEmpty(long.MaxValue).Max();
            if (group.Any(x => !existingIds.Contains(x.Id) && x.Id < maxRealId))
            {
                total += dapper.Execute(
                    $"DELETE FROM ZZZGachaItem WHERE Uid = @Uid AND GachaType = @GachaType AND {FillRecordSqlCondition};",
                    new { group.Key.Uid, group.Key.GachaType }, transaction);
            }
        }
        return total;
    }



    public override async Task ExportGachaLogAsync(long uid, string file, string format)
    {
        if (format is "excel")
        {
            await ExportAsExcelAsync(uid, file);
        }
        else
        {
            await ExportAsJsonAsync(uid, file);
        }
    }



    private async Task ExportAsJsonAsync(long uid, string output)
    {
        using var dapper = DatabaseService.CreateConnection();
        // 补全记录的 Id 是本地生成的，导出会污染其他工具的数据
        var list = dapper.Query<ZZZGachaItem>($"SELECT * FROM {GachaTableName} WHERE Uid = @uid AND NOT {FillRecordSqlCondition} ORDER BY Id;", new { uid }).ToList();
        DateTimeOffset time = DateTimeOffset.Now;
        var uigfObj = new UIGF3File<ZZZGachaItem>
        {
            Info = new UIAF3FileInfo
            {
                Uid = uid,
                Lang = list.LastOrDefault()?.Lang ?? "",
                ExportTimestamp = time.ToUnixTimeSeconds(),
                ExportTime = time.ToString("yyyy-MM-dd HH:mm:ss"),
                ExportAppVersion = AppConfig.AppVersion,
            },
            List = list,
        };
        using FileStream fs = File.Create(output);
        await JsonSerializer.SerializeAsync(fs, uigfObj, AppConfig.JsonSerializerOptions);
    }


    private async Task ExportAsExcelAsync(long uid, string output)
    {
        // 序号与垫抽按含补全记录的完整序列计算，但补全记录本身不导出
        var list = GetGachaLogItemEx(uid).Where(x => !ZZZGachaGapFiller.IsFillRecord(x.Id, x.Count)).ToList();
        var template = Path.Combine(AppContext.BaseDirectory, @"Assets\Template\ZZZGachaLog.xlsx");
        if (File.Exists(template))
        {
            await MiniExcel.SaveAsByTemplateAsync(output, template, new { list });
        }
    }




    public override long ImportGachaLog(string file)
    {
        var str = File.ReadAllText(file);
        var obj = JsonSerializer.Deserialize<UIGF3File<ZZZGachaItem>>(str);
        // info 缺失说明这不是旧版 JSON 结构，直接返回而不是抛 NullReferenceException
        if (obj?.Info is not null)
        {
            string lang = obj.Info.Lang ?? "";
            long uid = obj.Info.Uid;
            foreach (var item in obj.List)
            {
                if (item.Lang is null)
                {
                    item.Lang = lang;
                }
                if (item.Uid == 0)
                {
                    item.Uid = uid;
                }
            }
            var count = InsertGachaLogItems(obj.List.ToList<GachaLogItem>());
            // 成功导入调频记录 {count} 条
            InAppToast.MainWindow?.Success($"Uid {obj.Info.Uid}", string.Format(Lang.ZZZGachaService_ImportSignalSearchRecordsSuccessfully, count), 5000);
            return obj.Info.Uid;
        }
        return 0;
    }



    public override async Task<string> UpdateGachaInfoAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default)
    {
        var data = await _client.GetZZZGachaInfoAsync(gameBiz, lang, cancellationToken);
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        const string insertSql = """
            INSERT OR REPLACE INTO ZZZGachaInfo (Id, Name, Icon, Rarity, ElementType, Profession)
            VALUES (@Id, @Name, @Icon, @Rarity, @ElementType, @Profession);
            """;
        dapper.Execute(insertSql, data.List, t);
        t.Commit();
        return data.Language;
    }


    public override async Task<(string Language, int Count)> ChangeGachaItemNameAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default)
    {
        lang = await UpdateGachaInfoAsync(gameBiz, lang, cancellationToken);
        using var dapper = DatabaseService.CreateConnection();
        int count = dapper.Execute("""
             INSERT OR REPLACE INTO ZZZGachaItem (Uid, Id, Name, Time, ItemId, ItemType, RankType, GachaType, Count, Lang)
             SELECT item.Uid, item.Id, info.Name, Time, ItemId, ItemType, RankType, GachaType, Count, @Lang
             FROM ZZZGachaItem item INNER JOIN ZZZGachaInfo info ON item.ItemId = info.Id;
             """, new { Lang = lang });
        return (lang, count);
    }



    private static GachaLogItem ToGachaLogItem(long uid, ZZZGachaType gachaType, ZZZGachaRecordItem item, string language)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (uid == 0)
        {
            throw new ArgumentException("Uid cannot be 0.", nameof(uid));
        }
        if (gachaType.Value == 0)
        {
            throw new ArgumentException("GachaType cannot be 0.", nameof(gachaType));
        }
        if (item.Id == 0)
        {
            throw new ArgumentException("Id cannot be 0.", nameof(item));
        }
        if (item.ItemId == 0)
        {
            throw new ArgumentException("ItemId cannot be 0.", nameof(item));
        }
        if (item.Date == default)
        {
            throw new ArgumentException("Date cannot be default value.", nameof(item));
        }
        if (string.IsNullOrWhiteSpace(item.ItemName))
        {
            throw new ArgumentException("ItemName cannot be null or empty.", nameof(item));
        }
        if (string.IsNullOrWhiteSpace(item.ItemType))
        {
            throw new ArgumentException("ItemType cannot be null or empty.", nameof(item));
        }
        if (string.IsNullOrWhiteSpace(item.Rarity))
        {
            throw new ArgumentException("Rarity cannot be null or empty.", nameof(item));
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("Language cannot be null or empty.", nameof(language));
        }

        int rankType = item.Rarity.ToUpperInvariant() switch
        {
            "S" => 4,
            "A" => 3,
            "B" => 2,
            _ => 0,
        };
        if (rankType == 0)
        {
            throw new ArgumentException($"Invalid rarity value: {item.Rarity}.", nameof(item));
        }

        bool isChinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        return new ZZZGachaItem
        {
            Uid = uid,
            Id = item.Id,
            GachaType = gachaType,
            Name = item.ItemName,
            ItemType = item.ItemType.ToUpperInvariant() switch
            {
                "ITEM_TYPE_AVATAR" => isChinese ? "代理人" : item.ItemType,
                "ITEM_TYPE_WEAPON" => isChinese ? "音擎" : item.ItemType,
                "ITEM_TYPE_BANGBOO" => isChinese ? "邦布" : item.ItemType,
                _ => item.ItemType,
            },
            RankType = rankType,
            Time = item.Date,
            ItemId = item.ItemId,
            Count = 1,
            Lang = language,
        };
    }




    private class UIGFObj
    {
        public UIGFObj() { }

        public UIGFObj(long uid, List<ZZZGachaItem> list)
        {
            this.info = new UIGFInfo(uid, list);
            this.list = list;
        }

        public UIGFInfo info { get; set; }

        public List<ZZZGachaItem> list { get; set; }
    }


    private class UIGFInfo
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public long uid { get; set; }

        public string lang { get; set; }

        public int region_time_zone { get; set; } = 0;

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long export_timestamp { get; set; }

        public string export_app { get; set; } = "Starward";

        public string export_app_version { get; set; } = AppConfig.AppVersion ?? "";

        public string uigf_version { get; set; } = "v1.0";

        public UIGFInfo() { }

        public UIGFInfo(long uid, List<ZZZGachaItem> list)
        {
            this.uid = uid;
            lang = list.FirstOrDefault()?.Lang ?? "";
            export_timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        }
    }


}



/// <summary>
/// 官方统计补全的结果摘要。
/// </summary>
/// <param name="Uid">玩家 UID。</param>
/// <param name="Message">逐频段结果（多行），无变化时为「无需补全」。</param>
/// <param name="HasWarning">有频段因本地未同步或对不上而跳过。</param>
public sealed record ZZZGachaFillSummary(long Uid, string Message, bool HasWarning);
