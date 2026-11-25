using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.Database;
using Starward.Features.GameRecord.Genshin;
using Starward.Features.GameRecord.StarRail;
using Starward.Features.GameRecord.ZZZ;
using Starward.Features.ViewHost;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord.AutoRefresh;

/// <summary>
/// 自动更新战绩：软件常驻时检查一次，把到期的「账号 + 数据板块」在后台拉一遍写进本地库。
/// <para>
/// 存在的意义是补齐档案而不是省一次点击：米游社的战绩接口只给「当期 + 上期」，月报只给
/// <c>optional_month</c> 里那几个月，超窗口没刷过的那一期在本地库里就是永久空缺。
/// </para>
/// <para>
/// 排期单位是「任务」而不是「板块」：月报类板块拆成「当月」「上月」两个任务，各自开关、各自频率、
/// 各自「上次更新」与异常记录（当月适合几天一次跟进度，上月适合月初跑一次归档）。
/// </para>
/// <para>
/// 设计上刻意「不努力」：
/// 启动后先补足 45 秒缓冲才检查（避开首屏渲染、更新检查等启动高峰）；
/// 只在常驻启动后判一次到期，进程常驻期间不再跨日重判（错过的到期日等下次启动补上）；
/// 某个板块出错（风控、需要验证、断网）就记一条异常直接跳过，不重试、不自愈、不弹窗，
/// 用户在数据页的配置浮层里点进异常记录自己看。
/// </para>
/// <para>
/// 0.19 把旧的单个 <c>GameRecordService</c> 拆成了按游戏的服务 + <c>GameRecordRoleService</c>，
/// 这里按角色区服分派到对应的 per-game 服务，角色枚举与本地库读写仍走 <c>GameRecordRoleService</c>。
/// </para>
/// <para>
/// 生命周期跟着 DI 容器走：<c>AppConfig.ServiceProvider.cs</c> 里
/// <c>sc.AddSingleton&lt;AutoRecordRefreshService&gt;()</c>，
/// 调用点统一用 <c>AppConfig.GetService&lt;AutoRecordRefreshService&gt;()</c>。
/// </para>
/// </summary>
internal class AutoRecordRefreshService
{

    /// <summary>
    /// 等其它启动批量（后续接入的自动签到等常驻任务）的上限。对方卡住不能把战绩补档挂到进程退出。
    /// </summary>
    private static readonly TimeSpan StartupPreviousBatchWaitTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 常驻后至少缓冲这么久再开刷。签到无角色 / 全部关闭时首轮瞬间就完成，
    /// 那时首屏渲染、更新检查、抽卡名称迁移还都挤在启动这几十秒里，不能跟着一起打。
    /// </summary>
    private static readonly TimeSpan MinStartupDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 相邻两个任务之间的随机间隔（秒）：宁可一轮跑几分钟，也不要一次性打完。
    /// 0.19 的 per-game 服务里没有统一的请求节流入口，这里退化成「任务之间」的间隔，
    /// 月报翻页等任务内部的连续请求不再逐条节流。
    /// </summary>
    private const int MinRequestDelaySeconds = 3;

    /// <summary>见 <see cref="MinRequestDelaySeconds"/>。</summary>
    private const int MaxRequestDelaySeconds = 8;

    /// <summary>异常记录最多保留条数，超出丢弃最旧的。</summary>
    private const int MaxErrorCount = 50;

    /// <summary>异常记录在 Setting 表中的键名。</summary>
    private const string ErrorsSettingKey = "auto_record_refresh_errors";


    private readonly ILogger<AutoRecordRefreshService> _logger;

    private readonly GameRecordGenshinService _genshinService;

    private readonly GameRecordStarRailService _starRailService;

    private readonly GameRecordZZZService _zzzService;

    /// <summary>保证常驻检查每个进程只启动一次。</summary>
    private int _residentStarted;

    /// <summary>常驻宿主销毁（托盘窗口关闭 / 进程退出）时取消整轮检查，之后不再发起任何请求。</summary>
    private readonly CancellationTokenSource _stopSource = new();


    /// <summary>
    /// 初始化自动更新战绩服务。
    /// </summary>
    /// <param name="serviceProvider">DI 容器，用于取各游戏的数据服务。</param>
    public AutoRecordRefreshService(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<AutoRecordRefreshService>>();
        _genshinService = serviceProvider.GetRequiredService<GameRecordGenshinService>();
        _starRailService = serviceProvider.GetRequiredService<GameRecordStarRailService>();
        _zzzService = serviceProvider.GetRequiredService<GameRecordZZZService>();
    }


    /// <summary>
    /// 启动常驻检查：等其它启动批量结束后补足启动缓冲，再跑一轮到期的更新。幂等，每个进程只生效一次。
    /// <para>
    /// 挂着常驻宿主（<c>SystemTrayWindow</c>，唯一必然存在且不会销毁的窗口）上，
    /// 挂在唯一常驻的托盘窗口上：软件不常驻就不跑，窗口一走就停。
    /// </para>
    /// </summary>
    /// <param name="waitForPreviousStartupBatch">
    /// 其它启动期批量任务的完成任务（0.19 目前没有别的启动批量，恒为 null）
    /// ；为 null 时只等启动缓冲。
    /// </param>
    public void StartResident(Task? waitForPreviousStartupBatch = null)
    {
        if (Interlocked.Exchange(ref _residentStarted, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() => RunResidentCheckAsync(waitForPreviousStartupBatch, _stopSource.Token));
    }


    /// <summary>
    /// 常驻宿主消失（托盘窗口关闭）时调用：取消还没跑完的检查，之后不再发起任何请求。
    /// </summary>
    public void StopResident()
    {
        try
        {
            _stopSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经随进程退出，无需处理
        }
    }


    /// <summary>
    /// 常驻检查：等前置批量 → 启动缓冲 → 跑一轮。只在启动后判一次到期，不跨日重判。
    /// </summary>
    /// <param name="waitForPreviousStartupBatch">前置启动批量任务。</param>
    /// <param name="cancellationToken">停止令牌，由 <see cref="StopResident"/> 触发。</param>
    private async Task RunResidentCheckAsync(Task? waitForPreviousStartupBatch, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForPreviousStartupBatchAsync(waitForPreviousStartupBatch, cancellationToken);
            await RunBatchAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 常驻宿主走了：不记日志、不再请求
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto record refresh resident check failed.");
        }
    }


    /// <summary>
    /// 等其它启动批量打完；超时或门闩本身出错都不再等。
    /// </summary>
    /// <param name="waitForPreviousStartupBatch">前置启动批量任务；为 null 时直接进入缓冲。</param>
    /// <param name="cancellationToken">停止令牌。</param>
    private async Task WaitForPreviousStartupBatchAsync(Task? waitForPreviousStartupBatch, CancellationToken cancellationToken)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        if (waitForPreviousStartupBatch is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupPreviousBatchWaitTimeout);
            try
            {
                await waitForPreviousStartupBatch.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 常驻宿主已经走了：保持取消语义，不再往下跑
                throw;
            }
            catch (OperationCanceledException)
            {
                // 等超时：只是不等了，接着补足启动下限
                _logger.LogWarning("Auto record refresh: the previous startup batch did not finish within {timeout}; starting anyway.", StartupPreviousBatchWaitTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto record refresh: waiting for the previous startup batch failed; starting anyway.");
            }
        }

        // 前置批量很快完成时这点间隔远不够避开启动高峰，补足下限；已经等超时的自然不再等
        TimeSpan remaining = MinStartupDelay - (DateTimeOffset.UtcNow - start);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }


    /// <summary>
    /// 读取全部异常记录，最新的在前。
    /// </summary>
    /// <returns>异常记录列表；没有记录时为空列表。</returns>
    public List<RecordRefreshError> GetErrors()
    {
        string? json = AppConfig.GetValue<string>(default, ErrorsSettingKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<RecordRefreshError>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto record refresh: parse error list failed.");
            return [];
        }
    }


    /// <summary>
    /// 清空异常记录。
    /// </summary>
    public void ClearErrors()
    {
        AppConfig.SetValue<string>(null, ErrorsSettingKey);
    }


    /// <summary>
    /// 遍历所有角色的所有数据板块，逐个更新已启用且到期的那些。
    /// <para>
    /// 每个板块独立成败：出错只记一条异常并跳过，不影响同一角色的其他板块，也不影响其他角色。
    /// 只有成功的板块才写「上次更新」，失败的下次启动还会再试一次。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新成功的板块数。</returns>
    private async Task<int> RunBatchAsync(CancellationToken cancellationToken)
    {
        List<GameRecordRole> roles;
        try
        {
            roles = GetAllGameRoles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto record refresh: load game roles failed.");
            return 0;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var targets = new List<(GameRecordRole Role, RecordRefreshItem Item, RecordRefreshMonthTarget MonthTarget)>();
        foreach (GameRecordRole role in roles)
        {
            foreach (RecordRefreshItem item in RecordRefreshItemExtensions.GetItems(role.GameBiz))
            {
                // 月报类在这里展开成「当月」「上月」两个独立任务，两者的开关与到期各判各的
                foreach (RecordRefreshMonthTarget monthTarget in item.GetMonthTargets())
                {
                    RecordRefreshConfig config = RecordRefreshConfigStore.Load(role.GameBiz, role.Uid, item, monthTarget);
                    if (!config.Enabled)
                    {
                        continue;
                    }
                    if (!config.IsDue(now))
                    {
                        continue;
                    }
                    targets.Add((role, item, monthTarget));
                }
            }
        }
        if (targets.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("Auto record refresh started for {count} target(s).", targets.Count);

        bool pacingStarted = false;
        async Task PaceAsync(CancellationToken token)
        {
            if (pacingStarted)
            {
                int seconds = Random.Shared.Next(MinRequestDelaySeconds, MaxRequestDelaySeconds + 1);
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
            else
            {
                pacingStarted = true;
            }
        }

        int success = 0;
        foreach ((GameRecordRole role, RecordRefreshItem item, RecordRefreshMonthTarget monthTarget) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PaceAsync(cancellationToken);
                await RefreshItemAsync(role, item, monthTarget, cancellationToken);
                // 这一轮跑了几分钟，其间用户可能在浮层里改过频率：只把 lastRun 写回最新那份，别拿轮次开始时的旧配置盖掉
                RecordRefreshConfig latest = RecordRefreshConfigStore.Load(role.GameBiz, role.Uid, item, monthTarget);
                latest.LastRunTicks = DateTimeOffset.UtcNow.UtcTicks;
                RecordRefreshConfigStore.Save(role.GameBiz, role.Uid, item, latest, monthTarget);
                RemoveError(role, item, monthTarget);
                NotifyCompleted(role, item, monthTarget, succeeded: true);
                success++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (miHoYoApiException ex)
            {
                // 风控 / 需要验证 / Cookie 失效：后台解不开，记一条给用户看，直接跳过这个任务
                _logger.LogWarning(ex, "Auto record refresh failed (biz {biz}, uid {uid}, item {item}, month {month}, retcode {code}).", role.GameBiz, role.Uid, item, monthTarget, ex.ReturnCode);
                RecordError(role, item, monthTarget, ex.ReturnCode, ex.Message);
                NotifyCompleted(role, item, monthTarget, succeeded: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto record refresh failed (biz {biz}, uid {uid}, item {item}, month {month}).", role.GameBiz, role.Uid, item, monthTarget);
                RecordError(role, item, monthTarget, 0, ex.Message);
                NotifyCompleted(role, item, monthTarget, succeeded: false);
            }
        }

        _logger.LogInformation("Auto record refresh finished, {success}/{total} succeeded.", success, targets.Count);
        return success;
    }


    /// <summary>
    /// 通知当前打开的数据页：红点与列表必须在 UI 线程更新。
    /// </summary>
    private static void NotifyCompleted(GameRecordRole role, RecordRefreshItem item, RecordRefreshMonthTarget monthTarget, bool succeeded)
    {
        var message = new RecordRefreshCompletedMessage(role.GameBiz, role.Uid, item, monthTarget, succeeded);
        var dispatcher = MainWindow.Current?.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }
        if (dispatcher.HasThreadAccess)
        {
            WeakReferenceMessenger.Default.Send(message);
            return;
        }
        dispatcher.TryEnqueue(() => WeakReferenceMessenger.Default.Send(message));
    }


    /// <summary>
    /// 跑一个任务。月报类按任务取当月或上月，再往前的月份留给用户手动补；
    /// 明细按现有接口的语义覆盖（条数一致时接口层会自行跳过），任务之间的间隔由外层统一控制。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="item">数据板块。</param>
    /// <param name="monthTarget">要取的月份；非月报板块恒为当月且不参与分支。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RefreshItemAsync(GameRecordRole role, RecordRefreshItem item, RecordRefreshMonthTarget monthTarget, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case RecordRefreshItem.SpiralAbyss:
                await _genshinService.RefreshSpiralAbyssInfoAsync(role, 1, cancellationToken);
                await _genshinService.RefreshSpiralAbyssInfoAsync(role, 2, cancellationToken);
                break;

            case RecordRefreshItem.ImaginariumTheater:
                await _genshinService.RefreshImaginariumTheaterInfoAsync(role, cancellationToken);
                break;

            case RecordRefreshItem.StygianOnslaught:
                await _genshinService.RefreshStygianOnslaughtInfosAsync(role, cancellationToken);
                break;

            case RecordRefreshItem.TravelersDiary:
                {
                    // 先拿一次当月：月份要以接口返回的 data_month 为准而不是本机日期（账号服务器与本机可能差一天），
                    // 顺带把当月摘要写库、拿到 optional_month
                    var summary = await _genshinService.GetTravelersDiarySummaryAsync(role);
                    int month = summary.DataMonth;
                    if (monthTarget is RecordRefreshMonthTarget.Previous)
                    {
                        month = GetPreviousMonth(summary.DataMonth, summary.OptionalMonth);
                        if (month <= 0)
                        {
                            LogPreviousMonthUnavailable(role, item, summary.DataMonth.ToString(CultureInfo.InvariantCulture));
                            break;
                        }
                        // 上月的统计摘要也要覆盖一遍，否则本地库里那个月还停在它当月时的半程快照
                        await _genshinService.GetTravelersDiarySummaryAsync(role, month);
                    }
                    if (month > 0)
                    {
                        await _genshinService.GetTravelersDiaryDetailAsync(role, month, 1);
                        await _genshinService.GetTravelersDiaryDetailAsync(role, month, 2);
                    }
                    break;
                }

            case RecordRefreshItem.ForgottenHall:
                await _starRailService.RefreshForgottenHallInfoAsync(role, 1, cancellationToken);
                await _starRailService.RefreshForgottenHallInfoAsync(role, 2, cancellationToken);
                break;

            case RecordRefreshItem.PureFiction:
                await _starRailService.RefreshPureFictionInfoAsync(role, 1, cancellationToken);
                await _starRailService.RefreshPureFictionInfoAsync(role, 2, cancellationToken);
                break;

            case RecordRefreshItem.ApocalypticShadow:
                await _starRailService.RefreshApocalypticShadowInfoAsync(role, 1, cancellationToken);
                await _starRailService.RefreshApocalypticShadowInfoAsync(role, 2, cancellationToken);
                break;

            case RecordRefreshItem.ChallengePeak:
                await _starRailService.RefreshStarRailChallengePeakDataAsync(role, cancellationToken);
                break;

            case RecordRefreshItem.SimulatedUniverse:
                await _starRailService.GetSimulatedUniverseInfoAsync(role, true);
                break;

            case RecordRefreshItem.TrailblazeCalendar:
                {
                    var summary = await _starRailService.GetTrailblazeCalendarSummaryAsync(role);
                    string? month = summary.DataMonth;
                    if (monthTarget is RecordRefreshMonthTarget.Previous)
                    {
                        month = GetPreviousMonth(summary.DataMonth, summary.OptionalMonth);
                        if (month is null)
                        {
                            LogPreviousMonthUnavailable(role, item, summary.DataMonth);
                            break;
                        }
                        await _starRailService.GetTrailblazeCalendarSummaryAsync(role, month);
                    }
                    if (!string.IsNullOrWhiteSpace(month))
                    {
                        await _starRailService.GetTrailblazeCalendarDetailAsync(role, month, 1, cancellationToken);
                        await _starRailService.GetTrailblazeCalendarDetailAsync(role, month, 2, cancellationToken);
                    }
                    break;
                }

            case RecordRefreshItem.ShiyuDefense:
                await _zzzService.RefreshShiyuDefenseInfoAsync(role, 1, cancellationToken);
                await _zzzService.RefreshShiyuDefenseInfoAsync(role, 2, cancellationToken);
                break;

            case RecordRefreshItem.DeadlyAssault:
                await _zzzService.RefreshDeadlyAssaultInfoAsync(role, 1, cancellationToken);
                await _zzzService.RefreshDeadlyAssaultInfoAsync(role, 2, cancellationToken);
                break;

            case RecordRefreshItem.InterKnotReport:
                {
                    var summary = await _zzzService.GetInterKnotReportSummaryAsync(role);
                    if (monthTarget is RecordRefreshMonthTarget.Previous)
                    {
                        string? previous = GetPreviousMonth(summary.DataMonth, summary.OptionalMonth);
                        if (previous is null)
                        {
                            LogPreviousMonthUnavailable(role, item, summary.DataMonth);
                            break;
                        }
                        // 明细要按目标月自己的 month_data 列数据类型，不能沿用当月那一份
                        summary = await _zzzService.GetInterKnotReportSummaryAsync(role, previous);
                    }
                    if (!string.IsNullOrWhiteSpace(summary.DataMonth) && summary.MonthData?.List is { Count: > 0 } list)
                    {
                        foreach (var detail in list)
                        {
                            await _zzzService.GetInterKnotReportDetailAsync(role, summary.DataMonth, detail.DataType, cancellationToken);
                        }
                    }
                    break;
                }
        }
    }


    /// <summary>
    /// 「上月」模式要拉的月份（原神用月份号 1–12）。
    /// <para>
    /// 一律从接口返回的当月往前推一个月，再用 <c>optional_month</c> 复核：那个列表就是接口愿意受理的月份，
    /// 不在里面的月份请求了也拿不到数据（新号开服首月就是这种情况）。
    /// </para>
    /// </summary>
    /// <param name="dataMonth">接口返回的当月月份号。</param>
    /// <param name="optionalMonths">接口给出的可查询月份；为空时不复核。</param>
    /// <returns>要拉的月份号；查不到时返回 0。</returns>
    private static int GetPreviousMonth(int dataMonth, List<int>? optionalMonths)
    {
        if (dataMonth is < 1 or > 12)
        {
            return 0;
        }
        int previous = dataMonth == 1 ? 12 : dataMonth - 1;
        if (optionalMonths is { Count: > 0 } && !optionalMonths.Contains(previous))
        {
            return 0;
        }
        return previous;
    }


    /// <summary>
    /// 「上月」模式要拉的月份（星铁 / 绝区零用 <c>yyyyMM</c>）。判定规则同 <see cref="GetPreviousMonth(int, List{int})"/>。
    /// </summary>
    /// <param name="dataMonth">接口返回的当月，形如 <c>202609</c>。</param>
    /// <param name="optionalMonths">接口给出的可查询月份；为空时不复核。</param>
    /// <returns>要拉的月份；查不到时返回 null。</returns>
    private static string? GetPreviousMonth(string? dataMonth, List<string>? optionalMonths)
    {
        if (!DateTime.TryParseExact(dataMonth, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime month))
        {
            return null;
        }
        string previous = month.AddMonths(-1).ToString("yyyyMM", CultureInfo.InvariantCulture);
        if (optionalMonths is { Count: > 0 } && !optionalMonths.Contains(previous))
        {
            return null;
        }
        return previous;
    }


    /// <summary>
    /// 选了「上月」但接口不给那个月：记一条日志就算这轮做完，不当失败也不写异常记录
    /// ——重试一百次结果也一样，留给下个月自然恢复。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="item">数据板块。</param>
    /// <param name="dataMonth">接口返回的当月。</param>
    private void LogPreviousMonthUnavailable(GameRecordRole role, RecordRefreshItem item, string? dataMonth)
    {
        _logger.LogInformation("Auto record refresh: previous month is not available (biz {biz}, uid {uid}, item {item}, data month {month}).", role.GameBiz, role.Uid, item, dataMonth);
    }


    /// <summary>
    /// 数据库中全部有 Cookie 的游戏角色（跨所有账号与游戏），按账号(Cookie)再按游戏排序。
    /// <para>
    /// <c>GameRecordRoleService.GetGameRoles</c> 只按单个 biz 查询，这里直接读表拿全量，避免为了枚举角色去改那个服务。
    /// 没有 Cookie 的角色跳过（它请求不了）。
    /// </para>
    /// </summary>
    /// <returns>可参与自动更新的角色列表。</returns>
    private static List<GameRecordRole> GetAllGameRoles()
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole ORDER BY Cookie, GameBiz;");
        return list.Where(x => !string.IsNullOrWhiteSpace(x.Cookie)).ToList();
    }


    /// <summary>
    /// 记录一条异常。同一个任务只保留最新一条，整体最多 <see cref="MaxErrorCount"/> 条。
    /// </summary>
    /// <param name="role">出错的角色。</param>
    /// <param name="item">出错的数据板块。</param>
    /// <param name="monthTarget">出错的月份任务。</param>
    /// <param name="returnCode">米哈游接口返回码；非接口错误为 0。</param>
    /// <param name="message">异常消息，保留服务端原文。</param>
    private void RecordError(GameRecordRole role, RecordRefreshItem item, RecordRefreshMonthTarget monthTarget, int returnCode, string? message)
    {
        try
        {
            List<RecordRefreshError> errors = GetErrors();
            errors.RemoveAll(x => x.Uid == role.Uid && x.GameBiz == role.GameBiz && x.Item == item && x.MonthTarget == monthTarget);
            errors.Insert(0, new RecordRefreshError
            {
                GameBiz = role.GameBiz,
                Uid = role.Uid,
                Nickname = role.Nickname,
                Item = item,
                MonthTarget = monthTarget,
                Time = DateTimeOffset.UtcNow,
                ReturnCode = returnCode,
                Message = message,
            });
            if (errors.Count > MaxErrorCount)
            {
                errors.RemoveRange(MaxErrorCount, errors.Count - MaxErrorCount);
            }
            AppConfig.SetValue(JsonSerializer.Serialize(errors), ErrorsSettingKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto record refresh: save error list failed.");
        }
    }


    /// <summary>
    /// 这个任务这次更新成功了，清掉它上次留下的异常记录。
    /// </summary>
    /// <param name="role">更新成功的角色。</param>
    /// <param name="item">更新成功的数据板块。</param>
    /// <param name="monthTarget">更新成功的月份任务。</param>
    private void RemoveError(GameRecordRole role, RecordRefreshItem item, RecordRefreshMonthTarget monthTarget)
    {
        try
        {
            List<RecordRefreshError> errors = GetErrors();
            if (errors.RemoveAll(x => x.Uid == role.Uid && x.GameBiz == role.GameBiz && x.Item == item && x.MonthTarget == monthTarget) > 0)
            {
                AppConfig.SetValue(JsonSerializer.Serialize(errors), ErrorsSettingKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto record refresh: clear error entry failed.");
        }
    }

}
