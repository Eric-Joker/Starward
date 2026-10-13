using System;
using System.Collections.Generic;

namespace Starward.Features.TimeNode;

/// <summary>
/// 时间节点整页快照（按游戏组装后的展示模型）。
/// </summary>
internal sealed class TimeNodeSnapshot
{

    public IReadOnlyList<TimeNodeSection> Sections { get; init; } = [];

}


/// <summary>
/// 一个展示分段（卡池 / 调频 / 热点活动）。
/// </summary>
internal sealed class TimeNodeSection
{

    public required string Title { get; init; }

    public IReadOnlyList<TimeNodeItem> Items { get; init; } = [];

}


/// <summary>
/// 倒计时展示粒度。
/// </summary>
/// <remarks>
/// 上游用两种粒度区分「还有 x 天 x 小时 x 分钟 x 秒」与「还剩 x 天 x 小时」；
/// 本次移植没有秒级的现成文案（Lang 里不允许新增资源），两种粒度都落到同一套天 / 小时 / 分钟文案。
/// 保留枚举是为了不改变上游的模型形状，将来补上秒级文案时可以直接接回。
/// </remarks>
public enum TimeNodeCountdownKind
{
    /// <summary>上游：精确到秒。</summary>
    Precise = 0,

    /// <summary>上游：精确到分钟。</summary>
    Coarse = 1,
}


/// <summary>
/// 单条时间节点（卡池或热点活动）。
/// </summary>
internal sealed class TimeNodeItem
{

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public string? LinkUrl { get; init; }

    /// <summary>
    /// 封面图（热点活动左侧大图）；卡池条目可为空。
    /// </summary>
    public string? CoverIcon { get; init; }

    public TimeNodeCountdownKind CountdownKind { get; init; }

    /// <summary>
    /// 未开始时优先显示的固定文案（百科 <c>content_before_act</c>）。
    /// </summary>
    public string? ContentBeforeAct { get; init; }

    /// <summary>
    /// 已换算到目标区服的开始时刻；热点活动可为空。
    /// </summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>
    /// 已换算到目标区服的结束时刻。
    /// </summary>
    public DateTimeOffset EndTime { get; init; }

    public IReadOnlyList<TimeNodeIcon> Icons { get; init; } = [];

}


/// <summary>
/// 条目内小图标（角色 / 武器等）。
/// </summary>
public sealed class TimeNodeIcon
{

    public string Url { get; set; } = "";

    /// <summary>
    /// 可选稀有度角标（如 s / a）。
    /// </summary>
    public string? Level { get; set; }

}
