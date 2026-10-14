using CommunityToolkit.Mvvm.ComponentModel;
using Starward.Core.Gacha;

namespace Starward.Features.Gacha;

/// <summary>
/// 卡池条目：包装 <see cref="IGachaType"/>，用于卡池筛选（勾选即显示该卡池的统计卡片）。
/// </summary>
public partial class GachaBanner : ObservableObject
{

    /// <summary>
    /// 底层卡池类型定义（来自 Core 层各游戏的 *GachaType 实现，如 GenshinGachaType、StarRailGachaType 等）。
    /// </summary>
    public IGachaType GachaType { get; private set; }


    /// <summary>
    /// 卡池类型的整数标识（对应数据库中的 GachaType / QueryType 字段）。
    /// </summary>
    public int Value => GachaType.Value;


    /// <summary>
    /// 该卡池的本地化显示名称（例如「角色活动祈愿」「群星跃迁」「独家频段」等）。
    /// </summary>
    public string ToLocalization() => GachaType.ToLocalization();


    /// <summary>
    /// 是否在卡池筛选中勾选（勾选 = 显示该卡池）。
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }


    /// <summary>
    /// 创建卡池条目。
    /// </summary>
    /// <param name="gachaType">来自 <see cref="GachaLogClient.QueryGachaTypes"/> 的卡池类型定义。</param>
    public GachaBanner(IGachaType gachaType)
    {
        GachaType = gachaType;
    }


}
