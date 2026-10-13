using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Starward.Core;
using Starward.Core.Gacha;
using Starward.Core.GameRecord;
using Starward.Features.Account;
using Starward.Features.Gacha.UIGF;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord;
using Starward.Features.GameRecord.Share;
using Starward.Features.Screenshot;
using Starward.Features.ViewHost;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Storage;
using Windows.System;
using Windows.UI;


namespace Starward.Features.Gacha;

public sealed partial class GachaLogPage : PageBase
{

    private readonly ILogger<GachaLogPage> _logger = AppConfig.GetLogger<GachaLogPage>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();

    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();

    private readonly PassportAccountService _passportAccountService = AppConfig.GetService<PassportAccountService>();

    private readonly GenshinBeyondGachaService _genshinBeyondGachaService = AppConfig.GetService<GenshinBeyondGachaService>();


    private GachaLogService _gachaLogService;


    /// <summary>卡片拖拽换位逻辑（按住统计区域拖动、悬停即换位、松手提交并持久化）。</summary>
    private readonly GachaStatsCardDragReorder _dragReorder;

    /// <summary>常驻抽卡卡片池：按卡池类型(GachaType)复用卡片实例。</summary>
    private readonly Dictionary<int, FrameworkElement> _gachaCardPool = new();

    /// <summary>上次重建卡片所基于的统计数据引用；引用变化（切 UID / 数据刷新）时才整批重建卡片。</summary>
    private List<GachaTypeStats>? _reconciledStatsSource;

    /// <summary>当前记录区视图模式（列表 / 紧凑图标网格）；全局持久化，新建卡片与分享图均按此模式。</summary>
    private GachaRecordViewMode _recordViewMode;

    /// <summary>抽卡统计卡片的固定宽度。</summary>
    private const double GachaStatsCardWidth = 262;

    /// <summary>记录区视图模式的设置键（与 TZ 的 AppConfig.GachaRecordViewMode 同名同值，便于后续迁移）。</summary>
    private const string SettingKeyGachaRecordViewMode = "GachaRecordViewMode";



    public GachaLogPage()
    {
        // 先读出视图模式再给 Segmented 赋选中项：赋值触发的 SelectionChanged 因模式未变而被忽略，不会提前重建卡片。
        _recordViewMode = ReadRecordViewMode();
        this.InitializeComponent();
        _dragReorder = new GachaStatsCardDragReorder(ScrollViewer_GachaStats, Grid_GachaStats, StackPanel_GachaStats, SaveGachaCardOrder);
        Segmented_RecordViewMode.SelectedIndex = (int)_recordViewMode;
    }



    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        GachaTypeText = GachaLogService.GetGachaLogText(CurrentGameBiz);
        // 旧版导出对应的标准：原神是 UIGF v3.0，星铁是 SRGF v1.0，
        // 绝区零没有对应标准，Starward 只是套用了同样的结构，故仍标为 JSON
        LegacyFormatName = CurrentGameBiz.Game switch
        {
            GameBiz.hk4e => "UIGF v3.0",
            GameBiz.hkrpg => "SRGF v1.0",
            _ => "JSON",
        };
        LegacyImportText = string.Format(Lang.GachaLogPage_ImportFrom0, LegacyFormatName);
        if (CurrentGameBiz.Game == GameBiz.hk4e)
        {
            EnableGenshinGachaItemStats = true;
            _gachaLogService = AppConfig.GetService<GenshinGachaService>();
            Image_Emoji.Source = new BitmapImage(AppConfig.EmojiPaimon);
            // 原神国服 / B 服可以用工具箱角色的 stoken 直接换 authkey；国际服没有对应接口。
            if (CurrentGameBiz.Value is GameBiz.hk4e_cn or GameBiz.hk4e_bilibili)
            {
                MenuFlyoutItem_SyncFromMiyoushe.Visibility = Visibility.Visible;
                MenuFlyoutItem_SyncFromMiyousheAll.Visibility = Visibility.Visible;
            }
        }
        if (CurrentGameBiz.Game == GameBiz.hkrpg)
        {
            EnableStarRailGachaItemStats = true;
            _gachaLogService = AppConfig.GetService<StarRailGachaService>();
            Image_Emoji.Source = new BitmapImage(AppConfig.EmojiPom);
        }
        if (CurrentGameBiz.Game == GameBiz.nap)
        {
            EnableZZZGachaItemStats = true;
            IsZZZGachaStatsCardVisible = true;
            _gachaLogService = AppConfig.GetService<ZZZGachaService>();
            Image_Emoji.Source = new BitmapImage(AppConfig.EmojiBangboo);
            MenuFlyoutItem_CloudGame.Visibility = Visibility.Collapsed;
            if (CurrentGameBiz.Value is GameBiz.nap_cn or GameBiz.nap_bilibili)
            {
                MenuFlyoutItem_SyncFromMiyoushe.Visibility = Visibility.Visible;
                MenuFlyoutItem_SyncFromMiyousheAll.Visibility = Visibility.Visible;
            }
            if (CurrentGameBiz == GameBiz.nap_global)
            {
                MenuFlyoutItem_SyncFromHoYoLAB.Visibility = Visibility.Visible;
                MenuFlyoutItem_SyncFromHoYoLABAll.Visibility = Visibility.Visible;
            }
        }
        if (CurrentGameBiz.IsGlobalServer())
        {
            MenuFlyoutItem_CloudGame.Visibility = Visibility.Collapsed;
        }
    }


    public bool IsZZZGachaStatsCardVisible { get; set => SetProperty(ref field, value); }


    public string GachaTypeText { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 旧版（非 UIGF v4.x）导出格式名称，随游戏变化
    /// </summary>
    public string LegacyFormatName { get; set => SetProperty(ref field, value); } = "JSON";


    /// <summary>
    /// 旧版导入按钮文字，如「从 UIGF v3.0 导入」
    /// </summary>
    public string LegacyImportText { get; set => SetProperty(ref field, value); } = "";


    public ObservableCollection<long> UidList { get; set => SetProperty(ref field, value); }


    [ObservableProperty]
    public partial long? SelectUid { get; set; }
    partial void OnSelectUidChanged(long? value)
    {
        AppConfig.SetLastUidInGachaLogPage(CurrentGameBiz.Game, value ?? 0);
        UpdateGachaTypeStats(value);
        ShareGachaImageCommand.NotifyCanExecuteChanged();
    }


    /// <summary>分享图生成进行中时为 true，用于禁用分享按钮。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShareGachaImageCommand))]
    public partial bool IsSharingGachaImage { get; set; }




    protected override async void OnLoaded()
    {
        await Task.Delay(16);
        WeakReferenceMessenger.Default.Register<UpdateGachaLogMessage>(this, (s, m) =>
        {
            if (m.GameBiz == CurrentGameBiz)
            {
                User32.SetForegroundWindow((nint)this.XamlRoot.ContentIslandEnvironment.AppWindowId.Value);
                _ = UpdateGachaLogInternalAsync(m.Url);
            }
        });
        Grid_GachaStats.PointerWheelChanged += Grid_GachaStats_PointerWheelChanged;
        Initialize();
        await UpdateWikiDataAsync();
    }



    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        Grid_GachaStats.PointerWheelChanged -= Grid_GachaStats_PointerWheelChanged;
        ClearGachaCards();
        if (GachaItemStats is not null)
        {
            GachaItemStats.Clear();
            GachaItemStats = null;
        }
        ListView_GachaBanners.SelectionChanged -= ListView_GachaBanners_SelectionChanged;
        if (GachaBanners is not null)
        {
            GachaBanners.Clear();
            GachaBanners = null!;
        }
    }



    private void Initialize()
    {
        try
        {
            InitializeGachaBanners();
            SelectUid = null;
            UidList = new(_gachaLogService.GetUids());
            var lastUid = AppConfig.GetLastUidInGachaLogPage(CurrentGameBiz.Game);
            if (UidList.Contains(lastUid))
            {
                SelectUid = lastUid;
            }
            else
            {
                SelectUid = UidList.FirstOrDefault();
            }
            if (UidList.Count == 0)
            {
                StackPanel_Emoji.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize");
        }
    }



    /// <summary>
    /// 卡池卡片区域的滚轮事件处理：让滚轮「只影响竖直方向」。
    /// <para>拖拽中：交给 <see cref="_dragReorder"/>.HandleWheel 横向滚动，便于把卡片拖到视口外的卡池处。</para>
    /// <para>按住 Shift 或横向滚轮：仍按 main 原行为横向翻看卡池。</para>
    /// <para>其余：一律吞掉滚轮，阻止外层横向 <see cref="ScrollViewer_GachaStats"/> 把竖直滚轮转成横向滚动。</para>
    /// </summary>
    private void Grid_GachaStats_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_dragReorder.HandleWheel(e))
        {
            return;
        }
        var properties = e.GetCurrentPoint(Grid_GachaStats).Properties;
        if (properties.IsHorizontalMouseWheel || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            var delta = properties.MouseWheelDelta;
            ScrollViewer_GachaStats.ChangeView(ScrollViewer_GachaStats.HorizontalOffset - delta, null, null);
            e.Handled = true;
            return;
        }
        e.Handled = true;
    }



    private async Task UpdateWikiDataAsync()
    {
        try
        {
            string lang = string.IsNullOrWhiteSpace(GachaLanguage) ? System.Globalization.CultureInfo.CurrentUICulture.Name : GachaLanguage;
            await _gachaLogService.UpdateGachaInfoAsync(CurrentGameBiz, lang);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update wiki data {gameBiz}", CurrentGameBiz);
        }
    }




    [RelayCommand]
    private void OpenItemStatsPane()
    {
        SplitView_Content.IsPaneOpen = true;
    }




    #region Gacha Stats



    public bool EnableGenshinGachaItemStats { get; set => SetProperty(ref field, value); }

    public bool EnableStarRailGachaItemStats { get; set => SetProperty(ref field, value); }

    public bool EnableZZZGachaItemStats { get; set => SetProperty(ref field, value); }

    public List<GachaBanner> GachaBanners { get; set => SetProperty(ref field, value); }


    public List<GachaLogItemEx>? GachaItemStats { get; set => SetProperty(ref field, value); }


    private List<GachaTypeStats>? gachaTypeStats;


    private int errorCount = 0;


    private void InitializeGachaBanners()
    {
        GachaBanners = _gachaLogService.QueryGachaTypes.Select(x => new GachaBanner(x)).ToList();
        string? banner = AppConfig.GetDisplayGachaBanners(CurrentGameBiz.Game);
        if (!string.IsNullOrWhiteSpace(banner))
        {
            foreach (var item in banner.Split(','))
            {
                if (int.TryParse(item, out int type))
                {
                    if (GachaBanners.FirstOrDefault(x => x.Value == type) is GachaBanner gachaType)
                    {
                        ListView_GachaBanners.SelectedItems.Add(gachaType);
                    }
                }
            }
        }
        if (ListView_GachaBanners.SelectedItems.Count == 0)
        {
            foreach (var item in GachaBanners)
            {
                ListView_GachaBanners.SelectedItems.Add(item);
            }
        }
        if (CurrentGameBiz.Game is GameBiz.hkrpg && !AppConfig.GetValue(false, "SavedStarRailBannersAfterCollaborationStarting"))
        {
            if (ListView_GachaBanners.SelectedItems.Cast<GachaBanner>().FirstOrDefault(x => x.Value == 21) is null)
            {
                ListView_GachaBanners.SelectedItems.Add(GachaBanners.FirstOrDefault(x => x.Value == 21));
            }
            if (ListView_GachaBanners.SelectedItems.Cast<GachaBanner>().FirstOrDefault(x => x.Value == 22) is null)
            {
                ListView_GachaBanners.SelectedItems.Add(GachaBanners.FirstOrDefault(x => x.Value == 22));
            }
        }
        if (CurrentGameBiz.Game is GameBiz.nap && !AppConfig.GetValue(false, "SavedZZZBannersSinceVersion2"))
        {
            if (ListView_GachaBanners.SelectedItems.Cast<GachaBanner>().FirstOrDefault(x => x.Value == 102) is null)
            {
                ListView_GachaBanners.SelectedItems.Add(GachaBanners.FirstOrDefault(x => x.Value == 102));
            }
            if (ListView_GachaBanners.SelectedItems.Cast<GachaBanner>().FirstOrDefault(x => x.Value == 103) is null)
            {
                ListView_GachaBanners.SelectedItems.Add(GachaBanners.FirstOrDefault(x => x.Value == 103));
            }
        }
        ListView_GachaBanners.SelectionChanged -= ListView_GachaBanners_SelectionChanged;
        ListView_GachaBanners.SelectionChanged += ListView_GachaBanners_SelectionChanged;
    }


    private void ListView_GachaBanners_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            string value = string.Join(',', ListView_GachaBanners.SelectedItems.Cast<GachaBanner>().Select(x => x.Value));
            AppConfig.SetDisplayGachaBanners(CurrentGameBiz.Game, value);
            if (CurrentGameBiz.Game is GameBiz.hkrpg)
            {
                AppConfig.SetValue(true, "SavedStarRailBannersAfterCollaborationStarting");
            }
            if (CurrentGameBiz.Game is GameBiz.nap)
            {
                AppConfig.SetValue(true, "SavedZZZBannersSinceVersion2.5");
            }
            UpdateDisplayGachaTypeStats();
        }
        catch { }
    }



    /// <summary>
    /// 根据选中的 uid 加载并更新抽卡统计数据。
    /// <para>uid 为空或 0：清空统计数据、物品统计与卡片池，显示表情占位；</para>
    /// <para>否则调用服务 GetGachaTypeStats 获取各卡池统计与物品统计，然后刷新卡片显示并隐藏表情。</para>
    /// </summary>
    private void UpdateGachaTypeStats(long? uid)
    {
        try
        {
            if (uid is null or 0)
            {
                gachaTypeStats = null;
                _reconciledStatsSource = null;
                ClearGachaCards();
                GachaItemStats = null;
                StackPanel_Emoji.Visibility = Visibility.Visible;
            }
            else
            {
                (gachaTypeStats, GachaItemStats) = _gachaLogService.GetGachaTypeStats(uid.Value);
                UpdateDisplayGachaTypeStats();
                StackPanel_Emoji.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateGachaTypeStats");
        }
    }



    /// <summary>
    /// 算出「当前应该画在页面上的卡池统计列表」：先按勾选筛选，再按用户拖拽保存的顺序排序。它只算列表，不碰 UI。
    /// </summary>
    /// <returns>已选卡池的统计数据；无数据或未选卡池时返回空列表。</returns>
    private List<GachaTypeStats> GetDisplayedGachaTypeStats()
    {
        if (gachaTypeStats is null)
        {
            return [];
        }
        var stats = new List<GachaTypeStats>();
        var list = ListView_GachaBanners?.SelectedItems?.Cast<GachaBanner>().ToList() ?? [];
        if (list.Count == 0)
        {
            list = GachaBanners ?? [];
        }
        foreach (GachaBanner banner in list)
        {
            if (gachaTypeStats.FirstOrDefault(x => x.GachaType == banner.Value) is GachaTypeStats s)
            {
                stats.Add(s);
            }
        }
        return ApplySavedCardOrder(stats);
    }


    /// <summary>
    /// 按当前卡池筛选与用户拖拽保存的次序，刷新统计卡片的显示。
    /// </summary>
    private void UpdateDisplayGachaTypeStats()
    {
        if (gachaTypeStats is null)
        {
            return;
        }
        ReconcileGachaCards(GetDisplayedGachaTypeStats());
        ShareGachaImageCommand.NotifyCanExecuteChanged();
    }



    /// <summary>
    /// 将当前所选卡池统计渲染为分享图并打开内置图片查看器；记录区按当前视图（列表 / 紧凑图标网格）绘制。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShareGachaImage))]
    private async Task ShareGachaImageAsync()
    {
        try
        {
            if (SelectUid is not long uid || uid == 0)
            {
                return;
            }
            var stats = GetDisplayedGachaTypeStats();
            if (stats.Count == 0)
            {
                return;
            }

            IsSharingGachaImage = true;

            // 背景：与战绩分享图共用同一取图流程（视频背景在当前 main 上无可抓帧，退回强调色渐变底）。
            string? backgroundFile = await GameRecordShareHelper.PrepareBackgroundFileAsync(CurrentGameBiz);
            // 强调色须在 UI 线程读取；Win2D 离屏渲染放到后台线程，避免 Application.Current 跨线程 COM 异常。
            Color accentColor = GameRecordShareHelper.GetAccentColor();
            GachaRecordViewMode viewMode = _recordViewMode;
            string file = await Task.Run(async () =>
                await GachaShareImageRenderer.RenderAndSaveAsync(stats, CurrentGameBiz, backgroundFile, uid, accentColor, viewMode));
            await new ImageViewWindow2().ShowWindowAsync(this.XamlRoot.ContentIslandEnvironment.AppWindowId, file, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Share gacha image");
            InAppToast.MainWindow?.Error(ex);
        }
        finally
        {
            IsSharingGachaImage = false;
        }
    }


    /// <summary>是否允许触发分享图生成。</summary>
    private bool CanShareGachaImage()
        => !IsSharingGachaImage
           && SelectUid is > 0
           && gachaTypeStats is { Count: > 0 }
           && GetDisplayedGachaTypeStats().Count > 0;



    /// <summary>把当前展示卡片的卡池次序持久化（拖拽换位提交后回调）。次序 = 面板里可见卡片的子元素顺序。</summary>
    private void SaveGachaCardOrder()
    {
        try
        {
            var order = new List<int>();
            foreach (UIElement child in StackPanel_GachaStats.Children)
            {
                if (child is IGachaStatsDragCard card
                    && child is FrameworkElement { Visibility: Visibility.Visible }
                    && card.WarpTypeStats is { } stats)
                {
                    order.Add(stats.GachaType);
                }
            }
            AppConfig.SetValue(string.Join(',', order), GachaCardOrderSettingKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save gacha card order");
        }
    }


    /// <summary>卡片次序的设置键（与 TZ 的 AppConfig.SetGachaCardOrder 同键；AppConfig 不可改，故直接用通用设置 API）。</summary>
    private string GachaCardOrderSettingKey => $"gacha_card_order_{CurrentGameBiz.Game}";


    /// <summary>
    /// 按持久化的卡片次序对卡池统计稳定排序：已记录的卡池按记录次序在前，未记录的维持原有相对次序在后。
    /// </summary>
    private List<GachaTypeStats> ApplySavedCardOrder(List<GachaTypeStats> stats)
    {
        try
        {
            string? saved = AppConfig.GetValue<string>(null, GachaCardOrderSettingKey);
            if (string.IsNullOrWhiteSpace(saved))
            {
                return stats;
            }
            var order = saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(x => int.TryParse(x, out int v) ? v : int.MinValue)
                             .Where(x => x != int.MinValue)
                             .ToList();
            if (order.Count == 0)
            {
                return stats;
            }
            // OrderBy 为稳定排序：键相同（均未记录 = int.MaxValue）的项维持原有相对次序。
            return stats.OrderBy(s =>
            {
                int i = order.IndexOf(s.GachaType);
                return i < 0 ? int.MaxValue : i;
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply saved gacha card order");
            return stats;
        }
    }


    /// <summary>
    /// 按「最终展示次序」对齐常驻卡片池与面板子元素：复用已有卡片，只切 Visibility、用 <see cref="UIElementCollection.Move(uint, uint)"/> 重排。
    /// <para>核心优化：_gachaCardPool 作为常驻池，纯筛选（卡池勾选变化）时零重建，仅通过 Visibility + Move 调整可见顺序；</para>
    /// <para>仅当 gachaTypeStats 引用变化（切 UID、数据刷新/导入后）时才调用 ClearGachaCards 彻底重建。</para>
    /// </summary>
    private void ReconcileGachaCards(List<GachaTypeStats> ordered)
    {
        Panel panel = StackPanel_GachaStats;

        if (!ReferenceEquals(gachaTypeStats, _reconciledStatsSource))
        {
            ClearGachaCards();
            _reconciledStatsSource = gachaTypeStats;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            FrameworkElement card = GetOrCreateGachaCard(ordered[i]);
            card.Visibility = Visibility.Visible;

            int current = panel.Children.IndexOf(card);
            if (current < 0)
            {
                // 插入时用 Math.Min 防止索引越界（极端情况下面板子元素数量可能少于 i）。
                panel.Children.Insert(Math.Min(i, panel.Children.Count), card);
            }
            else if (current != i)
            {
                panel.Children.Move((uint)current, (uint)i);
            }
        }

        // 隐藏未被选中的卡片。
        var visible = new HashSet<int>();
        foreach (GachaTypeStats s in ordered)
        {
            visible.Add(s.GachaType);
        }
        foreach (KeyValuePair<int, FrameworkElement> kv in _gachaCardPool)
        {
            if (!visible.Contains(kv.Key))
            {
                kv.Value.Visibility = Visibility.Collapsed;
            }
        }
    }


    /// <summary>取池中卡片；没有则按当前游戏建对应类型卡片，赋数据/宽度、挂拖拽手柄后入池（尚未加入面板）。</summary>
    private FrameworkElement GetOrCreateGachaCard(GachaTypeStats stats)
    {
        if (_gachaCardPool.TryGetValue(stats.GachaType, out FrameworkElement? existing))
        {
            return existing;
        }
        IGachaStatsDragCard card;
        if (IsZZZGachaStatsCardVisible)
        {
            card = new ZZZGachaStatsCard();
        }
        else
        {
            card = new GachaStatsCard();
        }
        FrameworkElement element = (FrameworkElement)card;
        element.Width = GachaStatsCardWidth;
        element.DataContext = stats;     // 供拖拽手柄识别其卡池数据
        card.RecordViewMode = _recordViewMode; // 须在加入可视化树前赋值：紧凑模式要在记录列表生成容器前换好面板与模板
        card.WarpTypeStats = stats;      // 须在加入可视化树前赋值，卡片内 OneTime x:Bind 才能取到
        _dragReorder.Attach(card.DragHandle);
        _gachaCardPool[stats.GachaType] = element;
        return element;
    }


    /// <summary>
    /// 标题栏视图切换回调：选中项变化时持久化新视图并重建卡片。
    /// <para>构造函数赋初值、Segmented 首次应用模板时回写选中项也会触发本事件，模式未变则直接忽略；
    /// 单选模式下 Ctrl+点击会取消选中（索引为 -1），此时恢复为当前模式。</para>
    /// </summary>
    private void Segmented_RecordViewMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = Segmented_RecordViewMode.SelectedIndex;
        if (index < 0)
        {
            // 延后恢复，避免在选择变更回调里重入修改选中项。
            DispatcherQueue.TryEnqueue(() => Segmented_RecordViewMode.SelectedIndex = (int)_recordViewMode);
            return;
        }
        GachaRecordViewMode mode = index == (int)GachaRecordViewMode.Compact ? GachaRecordViewMode.Compact : GachaRecordViewMode.List;
        if (mode == _recordViewMode)
        {
            return;
        }
        _recordViewMode = mode;
        AppConfig.SetValue(_recordViewMode, SettingKeyGachaRecordViewMode);
        RebuildGachaCardsForViewMode();
    }


    /// <summary>读取持久化的记录区视图模式；未设置或不是已知取值时按列表处理。</summary>
    private static GachaRecordViewMode ReadRecordViewMode()
    {
        GachaRecordViewMode mode = AppConfig.GetValue(GachaRecordViewMode.List, SettingKeyGachaRecordViewMode);
        return mode == GachaRecordViewMode.Compact ? GachaRecordViewMode.Compact : GachaRecordViewMode.List;
    }


    /// <summary>
    /// 视图模式变化后重建全部卡片：清掉上次重建所基于的数据引用，让 <see cref="ReconcileGachaCards"/> 走与切换 UID 相同的整体重建路径，
    /// 新卡片在入树前按 <see cref="_recordViewMode"/> 配置记录区。
    /// </summary>
    /// <remarks>
    /// 不在运行中替换已有卡片的 ItemsPanel：整卡重建最稳。代价是最高/次一级稀有度选项卡与列表滚动位置复位。
    /// </remarks>
    private void RebuildGachaCardsForViewMode()
    {
        try
        {
            if (gachaTypeStats is null)
            {
                return;
            }
            _reconciledStatsSource = null;
            UpdateDisplayGachaTypeStats();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rebuild gacha cards for view mode {mode}", _recordViewMode);
        }
    }


    /// <summary>清空卡片池与面板子元素（数据刷新/切 uid/离开页面）：解绑手柄并移除子元素，触发卡片 Unloaded 释放其内部订阅。</summary>
    private void ClearGachaCards()
    {
        foreach (FrameworkElement card in _gachaCardPool.Values)
        {
            if (card is IGachaStatsDragCard dragCard)
            {
                _dragReorder.Detach(dragCard.DragHandle);
            }
        }
        _gachaCardPool.Clear();
        StackPanel_GachaStats?.Children.Clear();
    }



    #endregion



    #region Get Gacha



    /// <summary>
    /// 更新抽卡记录（SplitButton 主命令）。
    /// <para><b>输入：</b><paramref name="param"/> — 可选参数：
    /// <c>"cache"</c> 使用已缓存的 URL 更新；<c>"all"</c> 更新全部记录；<c>null</c> 从游戏 Web 缓存里校验并取用最新可用 URL。</para>
    /// </summary>
    [RelayCommand]
    private async Task UpdateGachaLogAsync(string? param = null)
    {
        try
        {
            string? url = null;
            if (param is "cache")
            {
                if (SelectUid is null or 0)
                {
                    return;
                }
                url = _gachaLogService.GetGachaLogUrlByUid(SelectUid.Value);
                if (string.IsNullOrWhiteSpace(url))
                {
                    // 无法找到 uid {uid} 的已缓存 URL
                    InAppToast.MainWindow?.Warning(null, string.Format(Lang.GachaLogPage_CannotFindSavedURLOfUid, SelectUid));
                    return;
                }
            }
            else
            {
                var path = GameLauncherService.GetGameInstallPath(CurrentGameId);
                if (!Directory.Exists(path))
                {
                    // 游戏未安装
                    InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_GameNotInstalled);
                    return;
                }
                InfoBar? validatingBar = null;
                try
                {
                    // 多候选 URL 校验：优先使用仍有效的 authkey，减少误删缓存
                    validatingBar = new InfoBar
                    {
                        Severity = InfoBarSeverity.Informational,
                        Message = Lang.GachaLogPage_ValidatingGachaUrl,
                        Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush,
                        IsOpen = true,
                    };
                    InAppToast.MainWindow?.Show(validatingBar);
                    url = await _gachaLogService.GetValidatedGachaLogUrlFromWebCacheAsync(CurrentGameBiz, path);
                }
                catch (GachaApiException ex) when (ex.IsAuthkeyExpired)
                {
                    errorCount++;
                    if (errorCount > 1 && IsGachaCacheFileExists())
                    {
                        errorCount = 0;
                        InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                              Lang.GachaLogPage_AlwaysFailedToGetGachaRecords,
                                                              Lang.GachaLogPage_RestartGameAfterDeletingTheCacheFolder,
                                                              Lang.GachaLogPage_DeleteCacheFolder,
                                                              () => _ = DeleteGachaCacheFolderAsync());
                    }
                    else
                    {
                        // authkey timeout
                        InAppToast.MainWindow?.Warning("Authkey Timeout", Lang.GachaLogPage_PleaseOpenTheGachaRecordsPageInGameAndTryAgain);
                    }
                    return;
                }
                finally
                {
                    // 无论成功、无候选还是异常，都关掉校验条（空 webCache 时也能关掉，TZ 2698c041）
                    if (validatingBar is not null)
                    {
                        validatingBar.IsOpen = false;
                    }
                }
                if (string.IsNullOrWhiteSpace(url))
                {
                    // 无法找到 URL，请在游戏中打开抽卡记录页面
                    errorCount++;
                    if (errorCount > 2 && IsGachaCacheFileExists())
                    {
                        errorCount = 0;
                        InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                              Lang.GachaLogPage_AlwaysFailedToGetGachaRecords,
                                                              Lang.GachaLogPage_RestartGameAfterDeletingTheCacheFolder,
                                                              Lang.GachaLogPage_DeleteCacheFolder,
                                                              () => _ = DeleteGachaCacheFolderAsync());
                    }
                    else
                    {
                        InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CannotFindURL);
                    }
                    return;
                }
            }
            await UpdateGachaLogInternalAsync(url, param is "all");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update gacha log");
            InAppToast.MainWindow?.Error(ex);
        }
    }




    private async Task UpdateGachaLogInternalAsync(string url, bool all = false)
    {
        try
        {
            var uid = await _gachaLogService.GetUidFromGachaLogUrl(url);
            var cancelSource = new CancellationTokenSource();
            var button = new Button
            {
                // 取消
                Content = Lang.Common_Cancel,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var infoBar = new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush,
                ActionButton = button,
            };
            button.Click += (_, _) =>
            {
                cancelSource.Cancel();
                // 操作已取消
                infoBar.Message = Lang.GachaLogPage_OperationCanceled;
                infoBar.ActionButton = null;
            };
            InAppToast.MainWindow?.Show(infoBar);
            var progress = new Progress<string>((str) => infoBar.Message = str);
            var newUid = await _gachaLogService.GetGachaLogAsync(url, all, GachaLanguage, progress, cancelSource.Token);
            infoBar.Title = $"Uid {newUid}";
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.ActionButton = null;
            if (SelectUid == uid)
            {
                UpdateGachaTypeStats(uid);
            }
            else
            {
                if (!UidList.Contains(uid))
                {
                    UidList.Add(uid);
                }
                SelectUid = uid;
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Get gacha log canceled");
        }
        catch (GachaApiException ex)
        {
            _logger.LogWarning("Request mihoyo api error: {error}", ex.Message);
            // 原铁 -101 绝 -1 均被 GachaApiException.IsAuthkeyExpired 覆盖
            if (ex.IsAuthkeyExpired)
            {
                // authkey timeout
                // 请在游戏中打开抽卡记录页面后再重试
                errorCount++;
                if (errorCount > 1 && IsGachaCacheFileExists())
                {
                    errorCount = 0;
                    InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                          Lang.GachaLogPage_AlwaysFailedToGetGachaRecords,
                                                          Lang.GachaLogPage_RestartGameAfterDeletingTheCacheFolder,
                                                          Lang.GachaLogPage_DeleteCacheFolder,
                                                          () => _ = DeleteGachaCacheFolderAsync());
                }
                else
                {
                    InAppToast.MainWindow?.Warning("Authkey Timeout", Lang.GachaLogPage_PleaseOpenTheGachaRecordsPageInGameAndTryAgain);
                }
            }
            else
            {
                InAppToast.MainWindow?.Warning(null, ex.Message);
            }
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogWarning("Request mihoyo api error: {error}", ex.Message);
            InAppToast.MainWindow?.Warning(null, ex.Message);
        }
    }


    [RelayCommand]
    private async Task SyncFromMiyousheAsync(string? param = null)
    {
        try
        {
            bool isZZZ = CurrentGameBiz.Game == GameBiz.nap;
            // 原神的 stoken 换 authkey 只有国服 / B 服有，国际服没有对应接口。
            bool isGenshinCN = CurrentGameBiz.Value is GameBiz.hk4e_cn or GameBiz.hk4e_bilibili;
            if (!isZZZ && !isGenshinCN)
            {
                // 入口只在受支持的区服显示（见菜单项显隐），这里只是兜底。
                InAppToast.MainWindow?.Warning(null, Lang.GameSettingPage_FeatureNotSupported);
                return;
            }
            GameBiz roleGameBiz = CurrentGameBiz.Value switch
            {
                GameBiz.nap_bilibili => GameBiz.nap_cn,
                GameBiz.hk4e_bilibili => GameBiz.hk4e_cn,
                _ => CurrentGameBiz,
            };
            var roles = _gameRecordService.GetGameRoles(roleGameBiz);
            if (roles.Count == 0)
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_PleaseLoginMiyousheAndAddZZZRole);
                WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(GameRecordPage)));
                return;
            }
            GameRecordRole? role;
            if (roles.Count == 1)
            {
                role = roles[0];
            }
            else
            {
                role = await SelectMiyousheRoleAsync(roles, roleGameBiz);
                if (role is null)
                {
                    return;
                }
            }
            if (string.IsNullOrWhiteSpace(role.Cookie))
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CurrentAccountMissingCookiePleaseReloginMiyoushe);
                return;
            }
            _gameRecordService.SetLastSelectGachaSyncRole(roleGameBiz, role);
            // 原神走 stoken 换 authkey：工具箱 Cookie 只带派生令牌（ltoken / cookie_token），
            // stoken + mid 在账号池里；手工粘贴的完整 Cookie 自带这两个键，优先用 Cookie 里的。
            string stoken = "";
            string mid = "";
            if (!isZZZ && !GameRecordService.TryGetSTokenAndMid(role.Cookie, out stoken, out mid))
            {
                (string Stoken, string Mid)? owned = _passportAccountService.GetStokenForGameRole(roleGameBiz, role.Uid);
                if (owned is null)
                {
                    InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CurrentAccountMissingCookiePleaseReloginMiyoushe);
                    return;
                }
                (stoken, mid) = owned.Value;
            }
            var cancelSource = new CancellationTokenSource();
            var button = new Button
            {
                // 取消
                Content = Lang.Common_Cancel,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var infoBar = new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush,
                ActionButton = button,
            };
            button.Click += (_, _) =>
            {
                cancelSource.Cancel();
                // 操作已取消
                infoBar.Message = Lang.GachaLogPage_OperationCanceled;
                infoBar.ActionButton = null;
            };
            InAppToast.MainWindow?.Show(infoBar);
            var progress = new Progress<string>((str) => infoBar.Message = str);
            long uid;
            if (isZZZ)
            {
                if (_gachaLogService is not ZZZGachaService zzzGachaService)
                {
                    throw new InvalidOperationException($"Current gacha service type is {_gachaLogService.GetType().Name}.");
                }
                uid = await zzzGachaService.GetGachaLogByGameRecordAsync(role, param is "all", GachaLanguage, progress, cancelSource.Token);
            }
            else
            {
                // 原神：stoken 换 authkey → 拼 public-operation URL → 复用既有分页逻辑，不必开 WebView 抓 URL
                infoBar.Message = Lang.GachaLogService_GettingUid;
                var authKey = await _gameRecordService.GenAuthKeyAsync(role, stoken, mid, cancelSource.Token);
                string url = GameRecordService.BuildGachaLogUrlFromAuthKey(CurrentGameBiz, role, authKey, GachaLanguage);
                uid = await _gachaLogService.GetGachaLogAsync(url, param is "all", GachaLanguage, progress, cancelSource.Token);
                // 战绩角色已有真实 UID：接口一条记录都没有时也把 URL 存下来，便于短时内增量更新
                long urlUid = uid > 0 ? uid : role.Uid;
                if (urlUid > 0)
                {
                    _gachaLogService.SaveGachaLogUrl(urlUid, url);
                }
            }
            infoBar.Title = $"Uid {uid}";
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.ActionButton = null;
            if (SelectUid == uid)
            {
                UpdateGachaTypeStats(uid);
            }
            else
            {
                if (!UidList.Contains(uid))
                {
                    UidList.Add(uid);
                }
                SelectUid = uid;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sync zzz gacha record from miyoushe canceled");
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogWarning(ex, "Sync zzz gacha record from miyoushe error ({retcode})", ex.ReturnCode);
            InAppToast.MainWindow?.Warning(null, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Sync zzz gacha record from miyoushe is not supported");
            InAppToast.MainWindow?.Warning(null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync zzz gacha record from miyoushe");
            InAppToast.MainWindow?.Error(ex);
        }
    }

    private async Task<GameRecordRole?> SelectMiyousheRoleAsync(List<GameRecordRole> roles, GameBiz roleGameBiz)
    {
        var items = roles.Select(role => new MiyousheRoleItem(role)).ToList();
        MiyousheRoleItem? selectedItem = null;
        if (SelectUid is long currentUid && currentUid > 0)
        {
            selectedItem = items.FirstOrDefault(x => x.Role.Uid == currentUid);
        }
        var selectedRole = _gameRecordService.GetLastSelectGachaSyncRoleOrTheFirstOne(roleGameBiz);
        selectedItem ??= items.FirstOrDefault(x =>
            selectedRole is not null
            && x.Role.Uid == selectedRole.Uid);
        var comboBox = new ComboBox
        {
            MinWidth = 420,
            ItemsSource = items,
            DisplayMemberPath = nameof(MiyousheRoleItem.DisplayText),
            SelectedItem = selectedItem ?? items.FirstOrDefault(),
        };
        var panel = new StackPanel
        {
            Spacing = 8,
        };
        panel.Children.Add(new TextBlock
        {
            Text = Lang.GachaLogPage_SelectMiyousheRoleDescription,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(comboBox);
        var dialog = new ContentDialog
        {
            Title = Lang.GachaLogPage_SelectMiyousheRole,
            Content = panel,
            PrimaryButtonText = Lang.Common_Confirm,
            SecondaryButtonText = Lang.Common_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = comboBox.SelectedItem is not null,
            XamlRoot = this.XamlRoot,
        };
        comboBox.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = comboBox.SelectedItem is not null;
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return null;
        }
        if (comboBox.SelectedItem is not MiyousheRoleItem item)
        {
            InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_PleaseSelectMiyousheRole);
            return null;
        }
        return item.Role;
    }


    private sealed class MiyousheRoleItem
    {
        public GameRecordRole Role { get; }

        public string DisplayText { get; }

        public MiyousheRoleItem(GameRecordRole role)
        {
            Role = role;
            DisplayText = $"{role.Nickname} ({role.Uid}) | {role.RegionName}";
        }
    }



    /// <summary>
    /// 通过 URL 更新抽卡记录：弹出对话框，预填当前 UID 已保存的 URL（若有），可直接确认或粘贴新 URL 后拉取。
    /// </summary>
    [RelayCommand]
    private async Task InputUrlAsync()
    {
        try
        {
            var textbox = new TextBox { MinWidth = 400 };
            // 预填当前 UID 已保存的 URL，合并「更新保存的 URL」与「输入 URL」
            if (SelectUid is > 0)
            {
                var saved = _gachaLogService.GetGachaLogUrlByUid(SelectUid.Value);
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    textbox.Text = saved;
                }
            }
            var dialog = new ContentDialog
            {
                // 输入 URL
                Title = Lang.GachaLogPage_InputURL,
                Content = textbox,
                // 确认
                PrimaryButtonText = Lang.Common_Confirm,
                // 取消
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var url = textbox.Text;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    await UpdateGachaLogInternalAsync(url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Input url");
            InAppToast.MainWindow?.Error(ex);
        }
    }




    [RelayCommand]
    private void OpenCloudGameWindow()
    {
        try
        {
            new CloudGameGachaWindow { GameBiz = CurrentGameBiz }.Activate();
        }
        catch { }
    }



    #endregion



    #region Gacha Setting Panel


    public string? GachaLanguage
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.GachaLanguage = value;
            }
        }
    } = AppConfig.GachaLanguage;



    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        try
        {
            if (SelectUid is null or 0)
            {
                return;
            }
            var url = _gachaLogService.GetGachaLogUrlByUid(SelectUid.Value);
            if (!string.IsNullOrWhiteSpace(url))
            {
                ClipboardHelper.SetText(url);
                FontIcon_CopyUrl.Glyph = "\uE8FB"; // accept
                await Task.Delay(1000);
                FontIcon_CopyUrl.Glyph = "\uE8C8";  // copy
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy url");
        }
    }



    [RelayCommand]
    private async Task ChangeGachaItemNameAsync()
    {
        try
        {
            string lang = string.IsNullOrWhiteSpace(GachaLanguage) ? System.Globalization.CultureInfo.CurrentUICulture.Name : GachaLanguage;
            (lang, int count) = await _gachaLogService.ChangeGachaItemNameAsync(CurrentGameBiz, lang);
            InAppToast.MainWindow?.Success(null, string.Format(Lang.GachaLogPage_0GachaItemsHaveBeenChangedToLanguage1, count, lang), 5000);
            UpdateGachaTypeStats(SelectUid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change gacha item name");
        }
    }



    [RelayCommand]
    private async Task DeleteUidAsync()
    {
        try
        {
            var uid = SelectUid;
            if (uid is null or 0)
            {
                return;
            }
            var dialog = new ContentDialog
            {
                // 警告
                Title = Lang.Common_Warning,
                // 即将删除 Uid {uid} 的所有抽卡记录，此操作不可恢复。
                Content = string.Format(Lang.GachaLogPage_DeleteGachaRecordsWarning, uid),
                // 删除
                PrimaryButtonText = Lang.Common_Delete,
                // 取消
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var count = _gachaLogService.DeleteUid(uid.Value);
                // 已删除 Uid {uid} 的抽卡记录 {count} 条
                InAppToast.MainWindow?.Success(null, string.Format(Lang.GachaLogPage_DeletedGachaRecordsOfUid, count, uid));
                Initialize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete uid");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    [RelayCommand]
    private async Task DeleteUidByTimeAsync()
    {
        try
        {
            var dialog = new DeleteGachaLogDialog
            {
                CurrentGameBiz = this.CurrentGameBiz,
                DefaultUid = this.SelectUid,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (dialog.Deleted)
            {
                UpdateGachaTypeStats(dialog.SelectUid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete uid");
        }
    }



    /// <summary>
    /// 一键删除游戏 webCaches（解决 authkey timeout）。游戏运行中拒绝删除；确认后递归删除并清理本地保存的 URL。
    /// 次要按钮为「在资源管理器中打开文件夹」。
    /// </summary>
    [RelayCommand]
    private async Task DeleteGachaCacheFolderAsync()
    {
        try
        {
            var installPath = GameLauncherService.GetGameInstallPath(CurrentGameId);
            if (!Directory.Exists(installPath))
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_GameNotInstalled);
                return;
            }

            var webCachesPath = GachaLogClient.GetWebCachesFolderPath(CurrentGameBiz, installPath);
            // 安全：路径必须落在安装目录下的 webCaches，防止误删
            string fullInstall = Path.GetFullPath(installPath);
            string fullCaches = Path.GetFullPath(webCachesPath);
            if (!fullCaches.StartsWith(fullInstall.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !fullCaches.EndsWith($"{Path.DirectorySeparatorChar}webCaches", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Refuse to delete unexpected webCaches path: {Path}", fullCaches);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = Lang.GachaLogPage_DeleteCacheFolderConfirmTitle,
                Content = Lang.GachaLogPage_DeleteCacheFolderConfirmContent,
                PrimaryButtonText = Lang.Common_Delete,
                SecondaryButtonText = Lang.GachaLogPage_OpenCacheFolder,
                CloseButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                await OpenGachaCacheFolderInExplorerAsync(webCachesPath);
                return;
            }
            if (result is not ContentDialogResult.Primary)
            {
                return;
            }

            var process = await _gameLauncherService.GetGameProcessAsync(CurrentGameId);
            if (process is not null)
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CannotDeleteCacheWhileGameIsRunning);
                return;
            }

            if (!Directory.Exists(webCachesPath))
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CacheFolderNotFound);
                return;
            }

            Directory.Delete(webCachesPath, recursive: true);
            _gachaLogService.DeleteSavedGachaLogUrl(SelectUid);
            errorCount = 0;
            InAppToast.MainWindow?.Success(null, Lang.GachaLogPage_CacheFolderDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete gacha cache file");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    /// <summary>
    /// 在资源管理器中打开并选中 webCaches 文件夹。
    /// </summary>
    /// <param name="webCachesPath">webCaches 完整路径。</param>
    private async Task OpenGachaCacheFolderInExplorerAsync(string webCachesPath)
    {
        if (!Directory.Exists(webCachesPath))
        {
            InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CacheFolderNotFound);
            return;
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(webCachesPath);
        var option = new FolderLauncherOptions();
        option.ItemsToSelect.Add(folder);
        await Launcher.LaunchFolderAsync(await folder.GetParentAsync(), option);
    }



    private bool IsGachaCacheFileExists()
    {
        try
        {
            var installPath = GameLauncherService.GetGameInstallPath(CurrentGameId);
            if (Directory.Exists(installPath))
            {
                var path = GachaLogClient.GetGachaCacheFilePath(CurrentGameBiz, installPath);
                return File.Exists(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check gacha cache file exists");
        }
        return false;
    }



    #endregion



    #region Import & Export



    [RelayCommand]
    private async Task ExportGachaLogAsync(string format)
    {
        try
        {
            if (SelectUid is null or 0)
            {
                return;
            }
            long uid = SelectUid.Value;
            if (!await EnsureExportWithoutGenshinBeyondAsync(uid))
            {
                return;
            }
            var ext = format switch
            {
                "excel" => "xlsx",
                "json" => "json",
                _ => "json"
            };
            var suggestName = $"Starward_Export_{CurrentGameBiz.Game}_{uid}_{DateTime.Now:yyyyMMddHHmmss}.{ext}";
            var file = await FileDialogHelper.OpenSaveFileDialogAsync(this.XamlRoot, suggestName, (ext, $".{ext}"));
            if (file is not null)
            {
                await _gachaLogService.ExportGachaLogAsync(uid, file, format);
                var storageFile = await StorageFile.GetFileFromPathAsync(file);
                var options = new FolderLauncherOptions();
                options.ItemsToSelect.Add(storageFile);
                await Launcher.LaunchFolderAsync(await storageFile.GetParentAsync(), options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export gacha log");
            InAppToast.MainWindow?.Error(ex);
        }
    }




    /// <summary>
    /// UIGF v3.0 不支持千星奇域。若当前 Uid 存在千星奇域记录，提示用户这部分记录不会被导出，
    /// 返回 false 表示用户选择取消，中断本次导出。
    /// </summary>
    private async Task<bool> EnsureExportWithoutGenshinBeyondAsync(long uid)
    {
        if (CurrentGameBiz.Game != GameBiz.hk4e)
        {
            return true;
        }
        if (!_genshinBeyondGachaService.GetUids().Contains(uid))
        {
            return true;
        }
        var dialog = new ContentDialog
        {
            Title = Lang.GachaLogPage_UIGF30DoesNotSupportMiliastraWonderlandOde,
            Content = Lang.GachaLogPage_UIGF30DoesNotSupportMiliastraWonderlandOdeDesc,
            PrimaryButtonText = Lang.Common_Continue,
            SecondaryButtonText = Lang.Common_Cancel,
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = this.XamlRoot,
        };
        return await dialog.ShowAsync() is ContentDialogResult.Primary;
    }




    [RelayCommand]
    private async Task ImportGachaLogAsync()
    {
        try
        {
            var file = await FileDialogHelper.PickSingleFileAsync(this.XamlRoot, ("Json", ".json"));
            if (File.Exists(file))
            {
                // 旧版导入只能解析 UIGF v3.0 / SRGF v1.0 结构（info + list），
                // UIGF v4.x 是 info.version + 每游戏档案数组，直接反序列化会得到 null 并抛异常。
                if (UIGF4FileDetector.IsUIGF4File(file))
                {
                    await ShowUIGF4ImportHintAsync();
                    return;
                }
                var uid = _gachaLogService.ImportGachaLog(file);
                if (uid == SelectUid)
                {
                    UpdateGachaTypeStats(uid);
                }
                else if (UidList.Contains(uid))
                {
                    SelectUid = uid;
                }
                else
                {
                    UidList.Add(uid);
                    SelectUid = uid;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import gacha log");
            InAppToast.MainWindow?.Error(ex);
        }
    }




    /// <summary>
    /// 选到的文件是 UIGF v4.x，引导用户走 v4.2 的导入入口。
    /// </summary>
    private async Task ShowUIGF4ImportHintAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Lang.GachaLogPage_ImportUIGF4UseUIGF4Window,
            Content = Lang.GachaLogPage_ImportUIGF4UseUIGF4WindowDesc,
            PrimaryButtonText = Lang.Common_Open,
            CloseButtonText = Lang.Common_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            OpenUIGF4Window();
        }
    }



    [RelayCommand]
    private void OpenUIGF4Window()
    {
        new UIGF4GachaWindow().Activate();
    }





    #endregion


}
