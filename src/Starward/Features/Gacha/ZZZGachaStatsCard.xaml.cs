using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Linq;


namespace Starward.Features.Gacha;

/// <summary>
/// 绝区零单频段抽卡统计卡片（S / A 记录列表）。
/// <para>承载统计摘要、S/A 记录列表切换、S 级垫数进度条，并实现 <see cref="IGachaStatsDragCard"/> 供页面级横向换位拖拽。</para>
/// <para>列表切换为无动画的 Visibility 切换（本分支不移植 TZ 的左右滑动过渡与保底条生长动画）。</para>
/// </summary>
public sealed partial class ZZZGachaStatsCard : UserControl, IGachaStatsDragCard
{

    /// <summary>当前记录列表选项卡索引（0 = S 级，1 = A 级）。</summary>
    private int _listIndex;

    /// <summary>正在由代码改写选项卡选中项时为 true，避免重入。</summary>
    private bool _applyingListVisibility;


    /// <summary>
    /// 初始化卡片 XAML，并挂接记录列表选项卡切换。
    /// </summary>
    public ZZZGachaStatsCard()
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


    /// <summary>拖拽手柄：tab 标签上方的频段统计信息区域，按住此处可拖动整张卡片换位。</summary>
    public FrameworkElement DragHandle => DragHandleBorder;


    public GachaTypeStats WarpTypeStats
    {
        get { return (GachaTypeStats)GetValue(WarpTypeStatsProperty); }
        set { SetValue(WarpTypeStatsProperty, value); }
    }

    // Using a DependencyProperty as the backing store for WarpTypeStats.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty WarpTypeStatsProperty =
        DependencyProperty.Register("WarpTypeStats", typeof(GachaTypeStats), typeof(ZZZGachaStatsCard), new PropertyMetadata(null));


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
    /// 紧凑模式下把 S/A 级记录列表换成 WrapPanel 面板 + 图标方块模板；列表模式保持 XAML 内联模板不动。
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


    private void Grid_Rarity5Item_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement ele && ele.Tag is GachaLogItemEx item)
            {
                if (WarpTypeStats?.List_5?.Any() ?? false)
                {
                    foreach (var l5 in WarpTypeStats.List_5)
                    {
                        l5.IsPointerIn = (l5.Name == item.Name);
                    }
                }
            }
        }
        catch { }
    }


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


    public void ResetGachaTypeTextFontSize()
    {
        TextBlock_GachaTypeText.FontSize = 16;
    }

}
