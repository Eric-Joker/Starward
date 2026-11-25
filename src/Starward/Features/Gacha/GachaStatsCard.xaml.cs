using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;


namespace Starward.Features.Gacha;

/// <summary>
/// 单卡池抽卡统计卡片（原神 / 星铁等非绝区零布局）。
/// <para>承载统计摘要、最高/次一级稀有度记录列表切换、垫数进度条，并实现 <see cref="IGachaStatsDragCard"/> 供页面级横向换位拖拽。</para>
/// <para>列表切换为无动画的 Visibility 切换（本分支不移植 TZ 的左右滑动过渡与保底条生长动画）。</para>
/// </summary>
public sealed partial class GachaStatsCard : UserControl, IGachaStatsDragCard
{

    /// <summary>
    /// 悬停判定在记录项边界外放宽的像素数，等于紧凑图标网格的方块间距（4px）再加一点余量。
    /// 指针在两张同名图标之间的缝隙里移动时仍算悬停在该记录项附近，不会因中途没有命中元素而把高亮清掉再设回来（描边闪烁）。
    /// </summary>
    private const double RecordItemHoverTolerance = 4.5;

    /// <summary>当前指针悬停的记录项根元素（列表行或紧凑图标方块）；null 表示当前没有悬停高亮。</summary>
    private FrameworkElement? _hoveredRecordElement;

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
        // 同名的相邻记录项之间（紧凑视图有 4px 方块间距）没有可命中的元素，靠 PointerEntered 切换时会把高亮清掉再设回来。
        // 这里在卡片级跟踪指针位置，只有离开悬停项附近才清除；滚动容器会处理指针事件，须 handledEventsToo 才能收到移动。
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Card_PointerMoved), true);
        PointerExited += Card_PointerExited;
        Unloaded += OnCardUnloaded;
    }


    /// <summary>
    /// 卡片卸载时解除事件订阅并清掉可能残留的悬停高亮（卡片常驻池复用，避免高亮状态带到下一次显示）。
    /// </summary>
    private void OnCardUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnCardUnloaded;
        Segmented_GachaItemList.SelectionChanged -= Segmented_GachaItemList_SelectionChanged;
        RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Card_PointerMoved));
        PointerExited -= Card_PointerExited;
        // 卡片移除时清掉模型上的高亮，避免重建后的新卡片带着残留描边出现。
        ClearRecordPointerIn();
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
    /// 卡池数据变化时按其最高稀有度重设本卡配色，并复位悬停跟踪状态（换绑数据后旧记录项已无意义）。
    /// </summary>
    /// <param name="d">卡片。</param>
    /// <param name="e">变更参数（未使用；旧记录项已不再展示，仅复位跟踪状态）。</param>
    private static void OnWarpTypeStatsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (GachaStatsCard)d;
        card._hoveredRecordElement = null;
        card.ApplyRarityTier();
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
        ClearRecordPointerIn();
    }


    /// <summary>
    /// 指针进入最高稀有度记录项时：按角色/武器名高亮同名项（设置 <see cref="GachaLogItemEx.IsPointerIn"/>），用于展开该行详情并弱化其余行。
    /// <para>离开记录项不再在这里清除高亮：紧凑视图的图标方块之间有 4px 间距，清除后要等进入相邻项才恢复，
    /// 相邻两项同名时描边会闪一下。改由 <see cref="Card_PointerMoved"/> 按指针与当前悬停项的位置关系判断。</para>
    /// </summary>
    /// <param name="sender">触发进入的列表项根元素；其 <c>Tag</c> 应为 <see cref="GachaLogItemEx"/>。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Grid_Rarity5Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is GachaLogItemEx item)
        {
            _hoveredRecordElement = element;
            WarpTypeStats?.SetPointerIn(item.Name);
        }
    }


    /// <summary>
    /// 指针进入次一级稀有度记录项时：按名称高亮同名项（逻辑同上，操作 <see cref="GachaTypeStats.List_4"/>）。
    /// </summary>
    /// <param name="sender">触发进入的列表项根元素；其 <c>Tag</c> 应为 <see cref="GachaLogItemEx"/>。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Grid_Rarity4Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is GachaLogItemEx item)
        {
            _hoveredRecordElement = element;
            WarpTypeStats?.SetPointerIn(item.Name);
        }
    }


    /// <summary>
    /// 指针在卡片内移动：把悬停判定更新到当前指针位置，超出悬停记录项（含图标方块间距容差）就清除高亮。
    /// </summary>
    /// <param name="sender">事件源（本卡片）。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateRecordPointerIn(e);
    }


    /// <summary>
    /// 指针离开卡片：清除悬停高亮。
    /// <para>卡片级的 PointerExited 只在指针真正离开卡片时触发（子元素的进出不会冒泡到这里，实测）；
    /// 但事件里的坐标可能是离开前的旧位置，不能据此判断指针是否还在卡片内，直接清除。</para>
    /// </summary>
    /// <param name="sender">事件源（本卡片）。</param>
    /// <param name="e">指针路由事件参数。</param>
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoveredRecordElement is null)
        {
            return;
        }
        ClearRecordPointerIn();
    }


    /// <summary>
    /// 按指针相对当前悬停记录项的位置更新高亮：仍在记录项边界外放宽 <see cref="RecordItemHoverTolerance"/> 的范围内则保留，否则清除。
    /// </summary>
    /// <param name="e">指针路由事件参数，用于取相对悬停记录项的当前位置。</param>
    private void UpdateRecordPointerIn(PointerRoutedEventArgs e)
    {
        FrameworkElement? hovered = _hoveredRecordElement;
        if (hovered is null)
        {
            return;
        }
        if (hovered.XamlRoot is null)
        {
            // 记录项已离开视觉树，没有可用的坐标空间。
            ClearRecordPointerIn();
            return;
        }
        var position = e.GetCurrentPoint(hovered).Position;
        if (position.X < -RecordItemHoverTolerance
            || position.Y < -RecordItemHoverTolerance
            || position.X > hovered.ActualWidth + RecordItemHoverTolerance
            || position.Y > hovered.ActualHeight + RecordItemHoverTolerance)
        {
            ClearRecordPointerIn();
        }
    }


    /// <summary>
    /// 清除记录项上的悬停高亮，并复位悬停跟踪状态。
    /// </summary>
    private void ClearRecordPointerIn()
    {
        _hoveredRecordElement = null;
        WarpTypeStats?.ClearPointerIn();
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
