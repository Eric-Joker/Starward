using Starward.Language;
using System;
using System.Collections.Generic;

namespace Starward.Features.Gacha;

/// <summary>
/// 单个卡池（祈愿/跃迁/频段）的统计数据。
/// 由 <see cref="GachaLogService.GetGachaTypeStats"/>（及各游戏子类的重写）填充，用于 GachaStatsCard / ZZZGachaStatsCard 等 UI 展示。
/// </summary>
public class GachaTypeStats
{

    /// <summary>
    /// 卡池类型（GachaType）的整数值。
    ///
    /// 不同游戏的取值与含义如下（来自各游戏的 *GachaType 常量）：
    ///
    /// 【原神 / Genshin (hk4e)】
    ///   100  - 新手祈愿 (NoviceWish)
    ///   200  - 常驻祈愿 (PermanentWish)
    ///   301  - 角色活动祈愿 (CharacterEventWish)
    ///   302  - 武器活动祈愿 (WeaponEventWish)
    ///   400  - 角色活动祈愿-2（与 301 合并统计，CharacterEventWish_2）
    ///   500  - 集录祈愿 (ChronicledWish)
    ///
    /// 【崩坏：星穹铁道 / Star Rail (hkrpg)】
    ///   1    - 群星跃迁 (StellarWarp，常驻)
    ///   2    - 始发跃迁 (DepartureWarp，新手/始发)
    ///   11   - 角色活动跃迁 (CharacterEventWarp)
    ///   12   - 光锥活动跃迁 (LightConeEventWarp)
    ///   21   - 角色联动跃迁 (CharacterCollaborationWarp)
    ///   22   - 光锥联动跃迁 (LightConeCollaborationWarp)
    ///
    /// 【绝区零 / ZZZ (nap)】
    ///   1    - 常驻频段 (StandardChannel)
    ///   2    - 独家频段 (ExclusiveChannel，代理人)
    ///   3    - 音擎频段 (WEngineChannel)
    ///   5    - 邦布频段 (BangbooChannel)
    ///   102  - 独家重映 (ExclusiveRescreening，与 2 共用非UP判定)
    ///   103  - 音擎回响 (WEngineReverberation，与 3 共用非UP判定)
    ///
    /// 注意：
    /// - ZZZ 的统计中，“5星”相关字段实际对应数据库 RankType==4（S级），“4星”对应 RankType==3（A级），Count_3 对应 RankType==2（B级）。
    /// - 原神 301 与 400 在统计时会被合并为同一个 GachaTypeStats（见 GenshinGachaService.GetGachaLogItemsByQueryType）。
    /// - 新手池（原神100、星铁2）在抽满固定次数（20/50）后，UI 不再显示当前 pity 进度。
    /// </summary>
    public int GachaType { get; set; }

    /// <summary>
    /// 统计卡片「最高稀有度」一栏实际对应的星级，默认 5。
    /// 当前 main 分支上所有走本类的卡池最高稀有度均为 5★（绝区零用 S/A/B 另行展示），
    /// 保留该字段是为了让卡片与分享图按 <see cref="TopRarity"/> 统一取文案与配色。
    /// </summary>
    public int TopRarity { get; set; } = 5;

    /// <summary>
    /// 最高稀有度一栏的星级文案，如「5★」。
    /// </summary>
    public string TopRarityText => $"{TopRarity}★";

    /// <summary>
    /// 次一级稀有度（4★ 列表一栏）的星级文案，如「4★」。
    /// </summary>
    public string SecondRarityText => $"{TopRarity - 1}★";

    /// <summary>
    /// 卡池类型的本地化显示名称（如“角色活动祈愿”、“群星跃迁”、“独家频段”等）。
    /// 来源于对应 *GachaType.ToLocalization() 的返回值（从 CoreLang 资源读取）。
    /// </summary>
    public string GachaTypeText { get; set; }

    /// <summary>
    /// 该卡池的总抽取次数（包含所有稀有度）。
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// 当前最高稀有度的小保底进度（pity）。
    /// 计算规则：从最近一次最高稀有度之后到最后一条记录的抽数；最后一条记录本身就是最高稀有度时为 0。
    /// ZZZ 中此字段实际统计 S 级（RankType==4）的 pity。
    /// </summary>
    public int Pity_5 { get; set; }

    /// <summary>
    /// 当前卡池最高稀有度的硬保底抽数，用作当前垫数进度条的最大值。
    /// 武器、光锥、音擎及邦布卡池为 80，其余卡池为 90（见 <see cref="GachaLogService.GetPityLimit"/>）。
    /// </summary>
    public int Pity_5_Max { get; set; } = 90;

    /// <summary>
    /// 是否显示当前最高稀有度垫数进度。
    /// 已抽满且不会再开放的一次性新手池为 false，其余有记录的卡池为 true。
    /// </summary>
    public bool ShowPityProgress { get; set; } = true;

    /// <summary>
    /// 下一个最高稀有度是否处于大保底状态（必为 UP）。
    /// 仅在 <see cref="HasUpItem"/> 为 true 时有展示意义。
    /// </summary>
    public bool IsNextPityGuaranteed { get; set; }

    /// <summary>
    /// 当前最高稀有度垫数及硬保底上限的展示文本，如「保底 45/90」。
    /// </summary>
    /// <remarks>
    /// TZ 使用 <c>Lang.GachaStatsCard_CurrentPity</c>（「当前 {0}/{1}」）；main 无该键，
    /// 复用最接近的 <c>Lang.GachaStatsCard_Pity</c> 并自行拼装抽数。
    /// </remarks>
    public string PityProgressText => $"{Lang.GachaStatsCard_Pity} {Pity_5}/{Pity_5_Max}";

    /// <summary>
    /// 下一次最高稀有度对应的小保底或大保底展示文本。非 UP 卡池由 UI 隐藏此文本。
    /// </summary>
    /// <remarks>
    /// TZ 使用 <c>Lang.GachaStatsCard_GuaranteedPity</c>（「大保底」）与 <c>Lang.GachaStatsCard_SmallPity</c>（「小保底」）；
    /// main 两者都没有，复用 <c>Lang.GachaStatsCard_Pity</c>，并用界面里既有的 “UP” 令牌区分大保底（下一次必为 UP）。
    /// </remarks>
    public string PityGuaranteeText => IsNextPityGuaranteed ? $"{Lang.GachaStatsCard_Pity} UP" : Lang.GachaStatsCard_Pity;

    /// <summary>
    /// 当前次一级稀有度的保底进度（pity）。
    /// 计算：总记录数 - 1 - 最后一次次一级稀有度在列表中的索引。
    /// ZZZ 中此字段实际统计 A 级（RankType==3）的 pity。
    /// </summary>
    public int Pity_4 { get; set; }

    /// <summary>
    /// 该卡池第一条记录的抽取时间（最早时间）。
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 该卡池最后一条记录的抽取时间（最晚时间）。
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// 最高稀有度获取数量。ZZZ 中实际为 S 级（RankType == 4）的数量。
    /// </summary>
    public int Count_5 { get; set; }

    /// <summary>
    /// UP 最高稀有度（当期 UP / 限定角色或武器）的获取数量。
    /// 仅当该卡池存在“非UP”机制（见 <see cref="GachaNoUp.Dictionary"/>）时才有值，否则为 0。
    /// </summary>
    public int Count_5_Up { get; set; }

    /// <summary>
    /// 次一级稀有度获取数量。ZZZ 中实际为 A 级（RankType == 3）的数量。
    /// </summary>
    public int Count_4 { get; set; }

    /// <summary>
    /// 第三级稀有度获取数量。ZZZ 中实际为 B 级（RankType == 2）的数量。
    /// </summary>
    public int Count_3 { get; set; }

    /// <summary>
    /// 最高稀有度出率（Count_5 / Count）。
    /// </summary>
    public double Ratio_5 { get; set; }

    /// <summary>
    /// 次一级稀有度出率（Count_4 / Count）。
    /// </summary>
    public double Ratio_4 { get; set; }

    /// <summary>
    /// 第三级稀有度出率（Count_3 / Count）。
    /// </summary>
    public double Ratio_3 { get; set; }

    /// <summary>
    /// 最高稀有度平均抽数（不含当前 pity）。
    /// 计算公式：(Count - Pity_5) / Count_5；<see cref="Count_5"/> 为 0 时保持 0，展示请用 <see cref="Average_5_Text"/>。
    /// </summary>
    public double Average_5 { get; set; }

    /// <summary>
    /// UP 最高稀有度的平均抽数（仅当 Count_5_Up &gt; 0 时有意义）。
    /// </summary>
    public double Average_5_Up { get; set; }

    /// <summary>
    /// 该卡池所有最高稀有度真实记录的列表（按时间倒序，最新记录在前）。
    /// ZZZ 中列表内容为 S 级记录。
    /// </summary>
    public List<GachaLogItemEx> List_5 { get; set; }

    /// <summary>
    /// 该卡池所有次一级稀有度真实记录的列表（按时间倒序，最新记录在前）。
    /// ZZZ 中为 A 级记录。
    /// </summary>
    public List<GachaLogItemEx> List_4 { get; set; }

    /// <summary>
    /// 最高稀有度平均抽数展示文本：有样本时形如「62.50」，无样本时为「—」。
    /// </summary>
    public string Average_5_Text => Count_5 == 0 ? "—" : $"{Average_5:F2}";

    /// <summary>
    /// 最高稀有度平均抽数描述文本的后缀：Count_5_Up &gt; 0 时返回 " / UP"，否则返回空字符串。
    /// 注意属性名拼写为 Avarage（沿用 main 既有拼写，勿改）。
    /// </summary>
    public string Avarage_5_Desc_Text => Count_5_Up == 0 ? "" : $" / UP";

    /// <summary>
    /// UP 最高稀有度的平均抽数与次数的格式化文本，如 " / 65.43 (12)"；无 UP 样本时为空字符串。
    /// 注意属性名拼写为 Avarage（沿用 main 既有拼写，勿改）。
    /// </summary>
    public string Avarage_5_Up_Text => Count_5_Up == 0 ? "" : $" / {Average_5_Up:F2} ({Count_5_Up})";


    /// <summary>
    /// 该卡池是否存在 UP（限定）机制，即是否有大小保底判定。
    /// 仅当卡池在 <see cref="GachaNoUp.Dictionary"/> 中存在配置时为 true。
    /// 用于决定统计卡片是否显示「不歪概率」一栏。
    /// </summary>
    public bool HasUpItem { get; set; }

    /// <summary>
    /// 小保底的总次数：即在非大保底状态下抽出的最高稀有度数量。
    /// 大保底（上次小保底歪了之后的必中 UP）不计入。
    /// </summary>
    public int FiftyFiftyCount { get; set; }

    /// <summary>
    /// 小保底不歪（抽到当期 UP）的次数。
    /// </summary>
    public int FiftyFiftyNoUpCount { get; set; }

    /// <summary>
    /// 小保底不歪概率 = <see cref="FiftyFiftyNoUpCount"/> / <see cref="FiftyFiftyCount"/>；无小保底样本时为 0。
    /// </summary>
    public double FiftyFiftyNoUpRate => FiftyFiftyCount == 0 ? 0 : (double)FiftyFiftyNoUpCount / FiftyFiftyCount;

    /// <summary>
    /// 「不歪概率」展示文本：有样本时形如「54.55% (6/11)」，无样本时为「—」。
    /// </summary>
    public string FiftyFiftyNoUpText => FiftyFiftyCount == 0 ? "—" : $"{FiftyFiftyNoUpRate:P2} ({FiftyFiftyNoUpCount}/{FiftyFiftyCount})";

    /// <summary>
    /// 小保底最多连续不歪（抽到 UP）次数。
    /// </summary>
    public int MaxFiftyFiftyUpStreak { get; set; }

    /// <summary>
    /// 小保底最多连续歪（未抽到 UP）次数。
    /// </summary>
    public int MaxFiftyFiftyMissStreak { get; set; }

    /// <summary>
    /// 是否显示「几连UP」胶囊标签：仅 UP 卡池、已有出金且最多连续不歪次数大于 0 时显示。
    /// </summary>
    public bool ShowFiftyFiftyUpStreakCapsule => HasUpItem && Count_5 > 0 && MaxFiftyFiftyUpStreak > 0;

    /// <summary>
    /// 是否显示「几连歪」胶囊标签：仅 UP 卡池、已有出金且最多连续歪次数大于 0 时显示。
    /// </summary>
    public bool ShowFiftyFiftyMissStreakCapsule => HasUpItem && Count_5 > 0 && MaxFiftyFiftyMissStreak > 0;

    /// <summary>
    /// 是否显示「几连UP / 几连歪」胶囊标签区域：任一胶囊可见时为 true。
    /// </summary>
    public bool ShowFiftyFiftyStreakCapsules => ShowFiftyFiftyUpStreakCapsule || ShowFiftyFiftyMissStreakCapsule;

    /// <summary>
    /// 「几连UP」胶囊展示文本，形如「UP×3」。
    /// </summary>
    /// <remarks>
    /// TZ 使用 <c>Lang.GachaStatsCard_UpStreak</c>（「{0}连UP」）；main 无该键，
    /// 这里用界面中既有的 “UP” 令牌拼装（卡片 XAML 本就硬编码 “up!”/“UP”），不去新增本地化资源。
    /// </remarks>
    public string MaxFiftyFiftyUpStreakText => $"UP×{MaxFiftyFiftyUpStreak}";

    /// <summary>
    /// 「几连歪」胶囊展示文本，形如「Miss×2」。
    /// </summary>
    /// <remarks>
    /// TZ 使用 <c>Lang.GachaStatsCard_MissStreak</c>（「{0}连歪」）；main 无该键，处理方式见 <see cref="MaxFiftyFiftyUpStreakText"/>。
    /// </remarks>
    public string MaxFiftyFiftyMissStreakText => $"Miss×{MaxFiftyFiftyMissStreak}";


    /// <summary>
    /// 清除最高与次一级稀有度记录上的悬停高亮（<see cref="GachaLogItemEx.IsPointerIn"/>）。
    /// 统计卡片在记录列表拖拽滚动结束时调用：拖拽期间指针被列表捕获，记录项收不到 PointerExited，松手后高亮可能残留。
    /// </summary>
    public void ClearPointerIn()
    {
        if (List_5 is not null)
        {
            foreach (GachaLogItemEx item in List_5)
            {
                item.IsPointerIn = false;
            }
        }
        if (List_4 is not null)
        {
            foreach (GachaLogItemEx item in List_4)
            {
                item.IsPointerIn = false;
            }
        }
    }

}
