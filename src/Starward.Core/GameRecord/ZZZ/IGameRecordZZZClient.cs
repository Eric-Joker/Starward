using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.GameRecord.ZZZ.DeadlyAssault;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.ZZZ.GachaStat;
using Starward.Core.GameRecord.ZZZ.InterKnotReport;
using Starward.Core.GameRecord.ZZZ.ShiyuDefense;
using Starward.Core.GameRecord.ZZZ.ThresholdSimulation;

namespace Starward.Core.GameRecord.ZZZ;

public interface IGameRecordZZZClient : IGameRecordClient
{

    /// <summary>
    /// 绝区零实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<ZZZDailyNote> GetZZZDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default);


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


    /// <summary>
    /// 养成指南 badge 登录：POST <c>common/badge/v1/login/account</c>，把响应 <c>Set-Cookie</c>（含 <c>e_nap_token</c>）合并进 Cookie。
    /// </summary>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">语言代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并 <c>e_nap_token</c> 后的 Cookie 串。</returns>
    public abstract Task<string> LoginZZZCultivateBadgeAsync(GameRecordRole role, string language, CancellationToken cancellationToken = default);


    /// <summary>
    /// 拉取绝区零官方抽卡统计（米游社 / HoYoLAB「抽卡记录」活动页）：总览 + 各频段全部历史 S 级记录。
    /// 覆盖账号全部历史，含超出记录接口保留期的部分；但只有 S 级、没有记录 Id。
    /// </summary>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">期望语言；国服记录名称固定中文，国际服随 Cookie 的 mi18nLang。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>总览、当前卡池与按频段分组的 S 级记录。</returns>
    public abstract Task<ZZZOfficialGachaStat> GetZZZOfficialGachaStatAsync(GameRecordRole role, string? language = null, CancellationToken cancellationToken = default);


    /// <summary>
    /// 官方抽卡统计总览（<c>nap_gacha_record/api/zzz/index</c>）。
    /// </summary>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">含 <c>e_nap_token</c> 的 Cookie。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>总览数据。</returns>
    public abstract Task<ZZZGachaStatIndex> GetZZZGachaStatIndexAsync(GameRecordRole role, string cookie, CancellationToken cancellationToken = default);


    /// <summary>
    /// 官方抽卡统计当前版本卡池（<c>cur_pool_stat</c>），按上下半拆分并带 S 级记录。
    /// </summary>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">含 <c>e_nap_token</c> 的 Cookie。</param>
    /// <param name="gachaType">ZZZGachaType 整数值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前版本卡池。</returns>
    public abstract Task<ZZZGachaPoolStatData> GetZZZGachaCurrentPoolStatAsync(GameRecordRole role, string cookie, int gachaType, CancellationToken cancellationToken = default);


    /// <summary>
    /// 官方抽卡统计历史版本（<c>hist_pool_stat</c>），每个版本一条，只返回出过 S 级的版本。
    /// </summary>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">含 <c>e_nap_token</c> 的 Cookie。</param>
    /// <param name="gachaType">ZZZGachaType 整数值。</param>
    /// <param name="versions">要查询的版本号（如 3.2），调用方保证每次不超过 8 个。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>历史版本的 S 级记录。</returns>
    public abstract Task<ZZZGachaPoolStatData> GetZZZGachaHistoryPoolStatAsync(GameRecordRole role, string cookie, int gachaType, IReadOnlyList<string> versions, CancellationToken cancellationToken = default);

}
