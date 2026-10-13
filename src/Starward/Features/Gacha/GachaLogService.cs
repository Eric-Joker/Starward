using Dapper;
using Microsoft.Extensions.Logging;
using MiniExcelLibs;
using Starward.Core;
using Starward.Core.Gacha;
using Starward.Core.Gacha.Genshin;
using Starward.Core.Gacha.StarRail;
using Starward.Core.Gacha.ZZZ;
using Starward.Features.Database;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.Gacha;


internal abstract class GachaLogService
{


    protected readonly ILogger<GachaLogService> _logger;


    protected readonly GachaLogClient _client;


    protected GachaLogService(ILogger<GachaLogService> logger, GachaLogClient client)
    {
        _logger = logger;
        _client = client;
    }



    protected abstract GameBiz CurrentGameBiz { get; }

    protected abstract string GachaTableName { get; }

    protected abstract List<GachaLogItemEx> GetGachaLogItemsByQueryType(IEnumerable<GachaLogItemEx> items, IGachaType type);

    public IReadOnlyCollection<IGachaType> QueryGachaTypes => _client.QueryGachaTypes;



    public static string GetGachaLogText(GameBiz biz)
    {
        return biz.ToGame().Value switch
        {
            GameBiz.hk4e => Lang.GachaLogService_WishRecords,
            GameBiz.hkrpg => Lang.GachaLogService_WarpRecords,
            GameBiz.nap => Lang.GachaLogService_SignalSearchRecords,
            _ => ""
        };
    }



    public virtual List<long> GetUids()
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<long>($"SELECT DISTINCT Uid FROM {GachaTableName};").ToList();
    }



    public virtual List<GachaLogItemEx> GetGachaLogItemEx(long uid)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GachaLogItemEx>($"SELECT * FROM {GachaTableName} WHERE Uid = @uid ORDER BY Id;", new { uid }).ToList();
        foreach (IGachaType type in QueryGachaTypes)
        {
            var l = GetGachaLogItemsByQueryType(list, type);
            int index = 0;
            int pity = 0;
            foreach (var item in l)
            {
                item.Index = ++index;
                item.Pity = ++pity;
                if (item.RankType == 5)
                {
                    pity = 0;
                }
            }
        }
        return list;
    }



    public virtual string? GetGachaLogUrlFromWebCache(GameBiz gameBiz, string path)
    {
        return GachaLogClient.GetGachaUrlFromWebCache(gameBiz, path);
    }



    /// <summary>
    /// 从网页缓存提取多个候选 URL，并依次调用官方接口校验 authkey，返回第一个有效的 URL。
    /// 全部候选都因 authkey 过期失败时抛出最后一次 <see cref="GachaApiException"/>，供 UI 引导用户清理缓存。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <param name="path">游戏安装根目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验通过的 URL；没有任何候选时返回 null。</returns>
    /// <exception cref="GachaApiException">所有候选的 authkey 均已过期时抛出。</exception>
    public virtual async Task<string?> GetValidatedGachaLogUrlFromWebCacheAsync(GameBiz gameBiz, string path, CancellationToken cancellationToken = default)
    {
        var candidates = GachaLogClient.GetGachaUrlCandidatesFromWebCache(gameBiz, path);
        if (candidates.Count == 0)
        {
            return null;
        }

        GachaApiException? lastAuthError = null;
        foreach (var url in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // uid==0 仍说明 authkey 有效（近 6 个月没有抽卡记录）；authkey 过期会抛 GachaApiException
                await _client.GetUidByGachaUrlAsync(url);
                return url;
            }
            catch (GachaApiException ex) when (ex.IsAuthkeyExpired)
            {
                _logger.LogInformation("Gacha authkey candidate expired, try next: {Message}", ex.Message);
                lastAuthError = ex;
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "Skip unparsable gacha URL candidate");
            }
        }

        if (lastAuthError is not null)
        {
            throw lastAuthError;
        }
        return null;
    }




    public virtual async Task<long> GetUidFromGachaLogUrl(string url)
    {
        long uid = await _client.GetUidByGachaUrlAsync(url);
        if (uid > 0)
        {
            SaveGachaLogUrl(uid, url);
        }
        return uid;
    }



    /// <summary>
    /// 将抽卡记录 URL 按 UID 持久化到本地（不发起网络请求）。
    /// 用于米游社 genAuthKey 同步成功后缓存 URL，避免重复请求。
    /// </summary>
    /// <param name="uid">玩家 UID；须 &gt; 0。</param>
    /// <param name="url">含 authkey 的抽卡 API / 页面 URL。</param>
    public virtual void SaveGachaLogUrl(long uid, string url)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO GachaLogUrl (GameBiz, Uid, Url, Time) VALUES (@GameBiz, @Uid, @Url, @Time);", new GachaLogUrl(CurrentGameBiz, uid, url));
    }



    public virtual string? GetGachaLogUrlByUid(long uid)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.QueryFirstOrDefault<string>("SELECT Url FROM GachaLogUrl WHERE Uid = @uid AND GameBiz = @GameBiz LIMIT 1;", new { uid, GameBiz = CurrentGameBiz });
    }



    /// <summary>
    /// 删除本地保存的抽卡 URL（用于清理已过期的 authkey，例如一键删除 webCaches 之后）。
    /// </summary>
    /// <param name="uid">可选 UID；为 null 或 &lt;= 0 时清除当前 GameBiz 下全部 URL。</param>
    public virtual void DeleteSavedGachaLogUrl(long? uid = null)
    {
        using var dapper = DatabaseService.CreateConnection();
        if (uid is > 0)
        {
            dapper.Execute("DELETE FROM GachaLogUrl WHERE Uid = @uid AND GameBiz = @GameBiz;", new { uid, GameBiz = CurrentGameBiz });
        }
        else
        {
            dapper.Execute("DELETE FROM GachaLogUrl WHERE GameBiz = @GameBiz;", new { GameBiz = CurrentGameBiz });
        }
    }



    protected abstract int InsertGachaLogItems(List<GachaLogItem> items);



    public virtual async Task<long> GetGachaLogAsync(string url, bool all, string? lang = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
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






    public virtual (List<GachaTypeStats> GachaStats, List<GachaLogItemEx> ItemStats) GetGachaTypeStats(long uid)
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
                    Count_5_Up = list.Count(x => x.RankType == 5 && x.IsUp),
                    Count_5 = list.Count(x => x.RankType == 5),
                    Count_4 = list.Count(x => x.RankType == 4),
                    Count_3 = list.Count(x => x.RankType == 3),
                    StartTime = list.First().Time,
                    EndTime = list.Last().Time,
                    Pity_5_Max = GetPityLimit(type),
                    ShowPityProgress = ShouldShowPityProgress(type, list.Count),
                };
                stats.Ratio_5 = (double)stats.Count_5 / stats.Count;
                stats.Ratio_4 = (double)stats.Count_4 / stats.Count;
                stats.Ratio_3 = (double)stats.Count_3 / stats.Count;
                stats.List_5 = list.Where(x => x.RankType == 5).Reverse().ToList();
                stats.List_4 = list.Where(x => x.RankType == 4).Reverse().ToList();
                stats.Pity_5 = list.Last().Pity;
                if (list.Last().RankType == 5)
                {
                    stats.Pity_5 = 0;
                }
                // 无 5 星样本时不计算平均，避免 NaN；展示侧用 Average_5_Text 显示「—」
                if (stats.Count_5 > 0)
                {
                    stats.Average_5 = (double)(stats.Count - stats.Pity_5) / stats.Count_5;
                }
                stats.Pity_4 = list.Count - 1 - list.FindLastIndex(x => x.RankType == 4);

                if (stats.Count_5_Up > 0)
                {
                    int c = stats.Count - stats.Pity_5;
                    stats.Average_5_Up = (double)c / stats.Count_5_Up;
                }

                // 「不歪概率」：统计小保底抽到当期 UP 的情况；角色池通常为 50/50，原神武器池为 75/25。
                stats.HasUpItem = GachaNoUp.Dictionary.ContainsKey($"{CurrentGameBiz}{type.Value}");
                if (stats.HasUpItem)
                {
                    (stats.FiftyFiftyCount, stats.FiftyFiftyNoUpCount, stats.MaxFiftyFiftyUpStreak, stats.MaxFiftyFiftyMissStreak, stats.IsNextPityGuaranteed) = CountFiftyFiftyNoUp(list, 5);
                }

                int pity_4 = 0;
                foreach (var item in list)
                {
                    pity_4++;
                    if (item.RankType == 4)
                    {
                        item.Pity = pity_4;
                        pity_4 = 0;
                    }
                }

                statsList.Add(stats);
            }
            groupStats = allItems.GroupBy(x => x.ItemId)
                                 .Select(x => { var item = x.First(); item.ItemCount = x.Count(); return item; })
                                 .OrderByDescending(x => x.RankType)
                                 .ThenByDescending(x => x.ItemCount)
                                 .ThenByDescending(x => x.Time)
                                 .ToList();
        }
        return (statsList, groupStats);
    }




    /// <summary>
    /// 获取指定卡池最高稀有度的硬保底抽数。
    /// </summary>
    /// <param name="gachaType">当前游戏下的卡池类型。</param>
    /// <returns>武器、光锥、音擎及邦布卡池返回 80，其余卡池返回 90。</returns>
    protected int GetPityLimit(IGachaType gachaType)
    {
        if ((CurrentGameBiz == GameBiz.hk4e && gachaType.Value == GenshinGachaType.WeaponEventWish)
            || (CurrentGameBiz == GameBiz.hkrpg && (gachaType.Value is StarRailGachaType.LightConeEventWarp or StarRailGachaType.LightConeCollaborationWarp))
            || (CurrentGameBiz == GameBiz.nap && (gachaType.Value is ZZZGachaType.WEngineChannel or ZZZGachaType.WEngineReverberation or ZZZGachaType.BangbooChannel)))
        {
            return 80;
        }
        return 90;
    }


    /// <summary>
    /// 判断指定卡池是否应显示当前最高稀有度垫数进度。
    /// </summary>
    /// <param name="gachaType">当前游戏下的卡池类型。</param>
    /// <param name="count">该卡池已有的抽卡记录总数。</param>
    /// <returns>已抽满的一次性新手池返回 false，其余卡池返回 true。</returns>
    protected bool ShouldShowPityProgress(IGachaType gachaType, int count)
    {
        return !(CurrentGameBiz == GameBiz.hk4e && gachaType.Value == GenshinGachaType.NoviceWish && count == 20)
            && !(CurrentGameBiz == GameBiz.hkrpg && gachaType.Value == StarRailGachaType.DepartureWarp && count == 50);
    }


    /// <summary>
    /// 统计小保底抽到当期 UP 的情况。
    /// 按时间正序遍历卡池记录，维护「下一个最高稀有度是否为大保底」的状态：
    /// 在非大保底状态下抽出的最高稀有度记为一次小保底，其中 <see cref="GachaLogItemEx.IsUp"/> 为 true 的记为一次「不歪」；
    /// 若小保底歪了（非 UP），则下一个最高稀有度为大保底（必出 UP），不计入小保底统计。
    /// </summary>
    /// <param name="orderedList">按时间（Id）正序排列的卡池全部记录。</param>
    /// <param name="highestRankType">最高稀有度对应的 RankType（原神/星铁为 5，绝区零 S 级为 4）。</param>
    /// <returns>(小保底次数, 小保底不歪次数, 最多连续不歪次数, 最多连续歪次数, 下一次是否为大保底)。</returns>
    protected static (int Count, int NoUpCount, int MaxUpStreak, int MaxMissStreak, bool IsNextGuaranteed) CountFiftyFiftyNoUp(IEnumerable<GachaLogItemEx> orderedList, int highestRankType)
    {
        int count = 0;
        int noUpCount = 0;
        int upStreak = 0;
        int missStreak = 0;
        int maxUpStreak = 0;
        int maxMissStreak = 0;
        bool guaranteed = false;
        foreach (GachaLogItemEx item in orderedList)
        {
            if (item.RankType != highestRankType)
            {
                continue;
            }
            if (guaranteed)
            {
                // 大保底必出 UP，不计入小保底统计，也不打断连胜/连歪计数
                guaranteed = false;
            }
            else
            {
                count++;
                if (item.IsUp)
                {
                    noUpCount++;
                    upStreak++;
                    missStreak = 0;
                    maxUpStreak = Math.Max(maxUpStreak, upStreak);
                }
                else
                {
                    missStreak++;
                    upStreak = 0;
                    maxMissStreak = Math.Max(maxMissStreak, missStreak);
                    guaranteed = true;
                }
            }
        }
        return (count, noUpCount, maxUpStreak, maxMissStreak, guaranteed);
    }






    public virtual int DeleteUid(long uid)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Execute($"DELETE FROM {GachaTableName} WHERE Uid = @uid;", new { uid });
    }



    public virtual int DeleteGachaLogByTime(long uid, DateTime begin, DateTime end)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Execute($"DELETE FROM {GachaTableName} WHERE Uid = @uid AND Time >= @begin AND Time <= @end;", new { uid, begin, end });
    }



    public abstract Task ExportGachaLogAsync(long uid, string file, string format);




    public abstract long ImportGachaLog(string file);




    public abstract Task<string> UpdateGachaInfoAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default);



    public abstract Task<(string Language, int Count)> ChangeGachaItemNameAsync(GameBiz gameBiz, string lang, CancellationToken cancellationToken = default);


}
