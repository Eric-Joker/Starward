
using CommunityToolkit.Mvvm.ComponentModel;
using Starward.Core.Gacha;
using Starward.Core.Gacha.Genshin;
using Starward.Core.Gacha.StarRail;
using Starward.Core.Gacha.ZZZ;

namespace Starward.Features.Gacha;

[INotifyPropertyChanged]
public partial class GachaLogItemEx : GachaLogItem
{

    /// <summary>
    /// 不要删除，导出 Excel 时有用
    /// </summary>
    public string IdText => Id.ToString();

    /// <summary>
    /// 相同保底卡池中的顺序
    /// </summary>
    public int Index { get; set; }

    public int Pity { get; set; }

    public string Icon { get; set; }

    public double Progress => (double)Pity / ((GachaType is GenshinGachaType.WeaponEventWish or StarRailGachaType.LightConeEventWarp or StarRailGachaType.LightConeCollaborationWarp or ZZZGachaType.WEngineChannel or ZZZGachaType.WEngineReverberation or ZZZGachaType.BangbooChannel) ? 80 : 90) * 100;


    public bool IsPointerIn { get; set => SetProperty(ref field, value); }

    public int ItemCount { get; set; }


    public bool HasUpItem { get; set; }

    public bool IsUp { get; set; }

    public double UpTextOpacity => IsUp ? 1 : 0;


    /// <summary>
    /// 紧凑视图图标方块的悬停提示：名称 + 换行 + 抽取时间。
    /// 方块内只画图标与抽数，名称与时间靠悬停提示补足（与列表视图行内的文案等价）。
    /// </summary>
    public string RecordTooltipText => $"{Name}\n{Time:yyyy-MM-dd HH:mm:ss}";

}
