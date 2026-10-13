using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Linq;
using Windows.UI;


namespace Starward.Features.Gacha;

/// <summary>
/// 单卡池抽卡统计卡片（原神 / 星铁等非绝区零布局）。
/// <para>承载统计摘要、最高/次一级稀有度记录列表切换、垫数进度条，并实现 <see cref="IGachaStatsDragCard"/> 供页面级横向换位拖拽。</para>
/// <para>列表切换为无动画的 Visibility 切换（本分支不移植 TZ 的左右滑动过渡与保底条生长动画）。</para>
/// </summary>
public sealed partial class GachaStatsCard : UserControl, IGachaStatsDragCard
{

    /// <summary>当前记录列表选项卡索引（0 = 最高稀有度，1 = 次一级稀有度）。</summary>
    private int _listIndex;

    /// <summary>正在由代码改写选项卡选中项时为 true，避免重入。</summary>
    private bool _applyingListVisibility;


    /// <summary>
    /// 初始化卡片 XAML，并挂接记录列表选项卡切换。
    /// </summary>
    public GachaStatsCard()
    {
        this.InitializeComponent();
        Segmented_GachaItemList.SelectionChanged += Segmented_GachaItemList_SelectionChanged;
        ApplySegmentedListVisibility();
        Unloaded += OnCardUnloaded;
    }


    /// <summary>
    /// 卡片卸载时解除事件订阅并清掉可能残留的悬停高亮（卡片常驻池复用，避免高亮状态带到下一次显示）。
    /// </summary>
    private void OnCardUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnCardUnloaded;
        Segmented_GachaItemList.SelectionChanged -= Segmented_GachaItemList_SelectionChanged;
        WarpTypeStats?.ClearPointerIn();
    }


    /// <summary>拖拽手柄：tab 标签上方的卡池统计信息区域，按住此处可拖动整张卡片换位。</summary>
    public FrameworkElement DragHandle => DragHandleBorder;


    /// <summary>
    /// 本卡片绑定的卡池统计数据（抽数、UP 率、最高/次一级稀有度列表等）。
    /// 页面复用卡片实例时会重新赋值；可为 null（未绑定数据时）。
    /// </summary>
    public GachaTypeStats WarpTypeStats
    {
        get { return (GachaTypeStats)GetValue(WarpTypeStatsProperty); }
        set { SetValue(WarpTypeStatsProperty, value); }
    }

    /// <summary><see cref="WarpTypeStats"/> 的依赖属性标识，供 XAML 绑定与样式使用。</summary>
    public static readonly DependencyProperty WarpTypeStatsProperty =
        DependencyProperty.Register("WarpTypeStats", typeof(GachaTypeStats), typeof(GachaStatsCard), new PropertyMetadata(null, OnWarpTypeStatsChanged));


    /// <summary>
    /// 卡池数据变化时按其最高稀有度重设本卡配色。
    /// </summary>
    /// <param name="d">卡片。</param>
    /// <param name="e">变更参数（未使用，以当前 <see cref="WarpTypeStats"/> 为准）。</param>
    private static void OnWarpTypeStatsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((GachaStatsCard)d).ApplyRarityTier();
    }


    /// <summary>
    /// 把「最高 / 次一级稀有度」画刷改成 <see cref="GachaTypeStats.TopRarity"/> 对应星级的颜色（取自 RarityNForegroundBrush），紧凑方块底板取同色 25% 透明度。
    /// </summary>
    /// <remarks>
    /// 直接改画刷 Color：这些画刷定义在本卡 Resources 中、每个实例各一份，卡内元素与记录模板引用的都是同一实例，改色即生效且不影响其他卡片。
    /// </remarks>
    private void ApplyRarityTier()
    {
        int top = WarpTypeStats?.TopRarity ?? 5;
        if (!Resources.TryGetValue($"Rarity{top}ForegroundBrush", out object topSource)
            || !Resources.TryGetValue($"Rarity{top - 1}ForegroundBrush", out object secondSource))
        {
            return;
        }
        Color topColor = ((SolidColorBrush)topSource).Color;
        Color secondColor = ((SolidColorBrush)secondSource).Color;
        ((SolidColorBrush)Resources["TopRarityForegroundBrush"]).Color = topColor;
        ((SolidColorBrush)Resources["SecondRarityForegroundBrush"]).Color = secondColor;
        ((SolidColorBrush)Resources["TopRarityTileBackgroundBrush"]).Color = Color.FromArgb(0x40, topColor.R, topColor.G, topColor.B);
        ((SolidColorBrush)Resources["SecondRarityTileBackgroundBrush"]).Color = Color.FromArgb(0x40, secondColor.R, secondColor.G, secondColor.B);
    }


    /// <summary>
    /// 记录区视图模式。须在卡片加入可视化树前由页面赋值；运行中切换视图由页面重建卡片完成，本卡片不做回切。
    /// </summary>
    public GachaRecordViewMode RecordViewMode
    {
        get;
        set
        {
            field = value;
            ApplyRecordViewMode();
        }
    }


    /// <summary>
    /// 紧凑模式下把两个记录列表换成 WrapPanel 面板 + 图标方块模板；列表模式保持 XAML 内联模板不动。
    /// </summary>
    /// <remarks>
    /// 只在 ItemsControl 生成容器之前调用：入树前 x:Bind 尚未给 ItemsSource 赋值，替换面板不会触发已有容器的重新生成。
    /// </remarks>
    private void ApplyRecordViewMode()
    {
        if (RecordViewMode != GachaRecordViewMode.Compact)
        {
            return;
        }
        var panel = (ItemsPanelTemplate)Resources["GachaRecordTilePanelTemplate"];
        ItemsRepeater_List_5.ItemsPanel = panel;
        ItemsRepeater_List_5.ItemTemplate = (DataTemplate)Resources["Rarity5TileTemplate"];
        ItemsRepeater_List_4.ItemsPanel = panel;
        ItemsRepeater_List_4.ItemTemplate = (DataTemplate)Resources["Rarity4TileTemplate"];
    }


    /// <summary>
    /// 记录列表选项卡切换：切换两个列表的 Visibility（无动画），把列表滚动位置复位到顶部，并清掉上一页的悬停高亮。
    /// <para>单选模式下 Ctrl+点击会取消选中（索引为 -1），此时延后恢复为当前索引。</para>
    /// </summary>
    private void Segmented_GachaItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingListVisibility)
        {
            return;
        }
        int index = Segmented_GachaItemList.SelectedIndex;
        if (index < 0)
        {
            // 延后恢复，避免在选择变更回调里重入修改选中项。
            _applyingListVisibility = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    Segmented_GachaItemList.SelectedIndex = _listIndex;
                }
                finally
                {
                    _applyingListVisibility = false;
                }
            });
            return;
        }
        _listIndex = index;
        ApplySegmentedListVisibility();
    }


    /// <summary>按当前选项卡索引显示对应记录列表，并复位滚动位置与悬停高亮。</summary>
    private void ApplySegmentedListVisibility()
    {
        bool showRarity5 = _listIndex == 0;
        if (ItemsRepeater_List_5 is not null && ItemsRepeater_List_4 is not null)
        {
            ItemsRepeater_List_5.Visibility = showRarity5 ? Visibility.Visible : Visibility.Collapsed;
            ItemsRepeater_List_4.Visibility = showRarity5 ? Visibility.Collapsed : Visibility.Visible;
        }
        // 切 tab 时滚动条复位（TZ e351eb5f 的行为；本分支不做左右滑动动画）
        try
        {
            ScrollViewer_GachaItemList?.ChangeView(null, 0, null, true);
        }
        catch { }
        WarpTypeStats?.ClearPointerIn();
    }


    /// <summary>
    /// 指针进入最高稀有度记录项时：按角色/武器名高亮同名项（设置 <see cref="GachaLogItemEx.IsPointerIn"/>），用于展开该行详情并弱化其余行。
    /// </summary>
    /// <param name="sender">触发进入的列表项根元素；其 <c>Tag</c> 应为 <see cref="GachaLogItemEx"/>。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Grid_Rarity5Item_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement ele && ele.Tag is GachaLogItemEx item)
            {
                if (WarpTypeStats?.List_5?.Any() ?? false)
                {
                    // 同名多条记录一并高亮（按 Name 匹配，非引用相等）。
                    foreach (var l5 in WarpTypeStats.List_5)
                    {
                        l5.IsPointerIn = (l5.Name == item.Name);
                    }
                }
            }
        }
        catch { }
    }


    /// <summary>
    /// 指针离开最高稀有度记录项时：清除列表上所有 <see cref="GachaLogItemEx.IsPointerIn"/>，恢复默认行布局。
    /// </summary>
    /// <param name="sender">触发离开的列表项根元素；其 <c>Tag</c> 应为 <see cref="GachaLogItemEx"/>。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Grid_Rarity5Item_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement ele && ele.Tag is GachaLogItemEx item)
            {
                if (WarpTypeStats?.List_5?.Any() ?? false)
                {
                    foreach (var l5 in WarpTypeStats.List_5)
                    {
                        l5.IsPointerIn = false;
                    }
                }
            }
        }
        catch { }
    }


    /// <summary>
    /// 指针进入次一级稀有度记录项时：按名称高亮同名项（逻辑同上，操作 <see cref="GachaTypeStats.List_4"/>）。
    /// </summary>
    /// <param name="sender">触发进入的列表项根元素；其 <c>Tag</c> 应为 <see cref="GachaLogItemEx"/>。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Grid_Rarity4Item_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement ele && ele.Tag is GachaLogItemEx item)
            {
                if (WarpTypeStats?.List_4?.Any() ?? false)
                {
                    foreach (var l5 in WarpTypeStats.List_4)
                    {
                        l5.IsPointerIn = (l5.Name == item.Name);
                    }
                }
            }
        }
        catch { }
    }


    /// <summary>
    /// 指针离开次一级稀有度记录项时：清除该列表上所有悬停高亮。
    /// </summary>
    /// <param name="sender">触发离开的列表项根元素；其 <c>Tag</c> 应为 <see cref="GachaLogItemEx"/>。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Grid_Rarity4Item_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement ele && ele.Tag is GachaLogItemEx item)
            {
                if (WarpTypeStats?.List_4?.Any() ?? false)
                {
                    foreach (var l5 in WarpTypeStats.List_4)
                    {
                        l5.IsPointerIn = false;
                    }
                }
            }
        }
        catch { }
    }


    /// <summary>
    /// 卡池名称文本被裁剪时的回调。历史上曾尝试逐级缩小字号，当前逻辑已禁用，保留空处理与 XAML 挂接以便日后恢复。
    /// </summary>
    /// <param name="sender">卡池名称 <see cref="TextBlock"/>。</param>
    /// <param name="args">是否发生裁剪的事件参数。</param>
    private void TextBlock_GachaTypeText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        //if (sender.FontSize == 16)
        //{
        //    sender.FontSize = 14;
        //}
        //if (sender.FontSize == 14)
        //{
        //    sender.FontSize = 12;
        //}
    }


    /// <summary>
    /// 将卡池名称字号重置为默认 16。卡片复用或数据刷新后若曾改过字号可调用此方法恢复。
    /// </summary>
    public void ResetGachaTypeTextFontSize()
    {
        TextBlock_GachaTypeText.FontSize = 16;
    }

}
