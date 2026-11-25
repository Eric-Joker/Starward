using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.Blackboard;
using Starward.Core.HoYoPlay;
using Starward.Features.Setting;
using Starward.Language;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.TimeNode;

/// <summary>
/// 游戏启动页右侧「时间节点」入口：图标按钮 + Flyout 卡池/活动倒计时。
/// </summary>
/// <remarks>
/// 入口只在「至少有一条当前/即将到来的节点」时显示，否则整块收起（不留空位）。
/// 判断依据是 Service 返回的快照——Service 在组装时已经丢掉已结束的条目，所以「有没有条目」就是「是否还在时效内」。
/// 上游用 Lottie 悬浮动画与 InstantTooltip；本分支改为静态 FontIcon，且按钮不带 Tooltip 文案
/// （Lang 里没有「时间节点」这个词条，本次移植不允许新增本地化资源）。
/// </remarks>
[INotifyPropertyChanged]
public sealed partial class TimeNodeButton : UserControl
{

    private readonly ILogger<TimeNodeButton> _logger = AppConfig.GetLogger<TimeNodeButton>();

    private readonly TimeNodeService _timeNodeService = AppConfig.GetService<TimeNodeService>();

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _countdownTimer;

    private CancellationTokenSource? _loadCts;


    public TimeNodeButton()
    {
        this.InitializeComponent();
        this.Visibility = Visibility.Collapsed;
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
    }


    /// <summary>
    /// 语言切换后刷新 x:Bind 文案，并重算倒计时格式串。
    /// </summary>
    private void OnLanguageChanged(object _, LanguageChangedMessage __)
    {
        this.Bindings.Update();
        RefreshAllCountdowns();
    }


    /// <summary>
    /// 当前启动页所选游戏。
    /// </summary>
    public GameId? CurrentGameId
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                if (IsLoaded)
                {
                    InitializeForCurrentGame();
                }
            }
        }
    }


    public ObservableCollection<TimeNodeSectionView> Sections { get; } = new();


    public bool IsEmpty { get; set => SetProperty(ref field, value); } = true;


    public bool IsLoading { get; set => SetProperty(ref field, value); }


    public string? ErrorMessage { get; set => SetProperty(ref field, value); }


    /// <summary>空态文案（入口正常不会露出来，仅防「弹层打开期间刷新变空」）。</summary>
    public string EmptyMessage { get; set => SetProperty(ref field, value); } = Lang.HoyolabToolboxPage_NoData;


    /// <summary>
    /// 该游戏是否有百科 app_sn（hk4e / hkrpg / nap）；
    /// 上游用 GameFeatureConfig.SupportTimeNode，本分支不新增那个开关，直接问 BlackboardClient。
    /// </summary>
    private static bool IsSupported(GameBiz gameBiz) => BlackboardClient.GetAppSn(gameBiz) is not null;


    private void Button_TimeNode_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeForCurrentGame();
    }


    private void Button_TimeNode_Unloaded(object sender, RoutedEventArgs e)
    {
        StopCountdownTimer();
        CancelLoad();
        Sections.Clear();
        ErrorMessage = null;
        IsEmpty = true;
        this.Visibility = Visibility.Collapsed;
    }


    /// <summary>
    /// 解析当前游戏并探测入口是否该出现。
    /// </summary>
    /// <remarks>
    /// 这里会走一次百科请求（与打开弹层走的是同一份 20 分钟内存缓存，所以随后打开弹层不会重复请求）。
    /// 这是「不满足时效条件就隐藏入口」这条要求的必要代价：没有数据就无从判断。
    /// 只在页面加载 / 切换游戏时各探测一次，不轮询、不常驻定时器。
    /// </remarks>
    private async void InitializeForCurrentGame()
    {
        if (CurrentGameId is null || !IsSupported(CurrentGameId.GameBiz))
        {
            this.Visibility = Visibility.Collapsed;
            StopCountdownTimer();
            Sections.Clear();
            IsEmpty = true;
            ErrorMessage = null;
            return;
        }
        await LoadSnapshotAsync(forceRefresh: false, updateVisibility: true);
    }


    private async void Flyout_TimeNode_Opened(object sender, object e)
    {
        // 定时器必须在 await 之前起：弹层若在加载期间被关闭，Closed 会先于续体执行，
        // 那时 StopCountdownTimer 停不到还没创建的定时器，续体再起一个就成了每秒空转的后台轮询
        StartCountdownTimer();
        await LoadSnapshotAsync(forceRefresh: false, updateVisibility: false);
    }


    private void Flyout_TimeNode_Closed(object sender, object e)
    {
        StopCountdownTimer();
        CancelLoad();
        // 弹层打开期间跨过结束时刻（或刷新后一条都不剩）时，入口要跟着收起
        IsEmpty = !HasTimelyItem(DateTimeOffset.Now);
        if (IsEmpty)
        {
            this.Visibility = Visibility.Collapsed;
        }
    }


    /// <summary>
    /// 是否还有未结束的节点。
    /// </summary>
    /// <remarks>
    /// Service 组装时已经丢掉已结束的条目，这里再按各条目的 EndTime 复核一次，
    /// 是为了兜住「快照取自 20 分钟内存缓存、期间条目已到期」的情况。
    /// </remarks>
    /// <param name="now">当前时刻。</param>
    /// <returns>存在未结束节点为 true。</returns>
    private bool HasTimelyItem(DateTimeOffset now) => Sections.Any(s => s.Items.Any(i => i.EndTime > now));


    /// <summary>
    /// 拉取快照并填充列表。
    /// </summary>
    /// <param name="forceRefresh">是否跳过内存缓存。</param>
    /// <param name="updateVisibility">是否顺带按结果决定入口显隐（页面加载时的探测为 true，弹层刷新为 false）。</param>
    private async Task LoadSnapshotAsync(bool forceRefresh, bool updateVisibility)
    {
        if (CurrentGameId is null)
        {
            return;
        }

        CancelLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        CancellationToken ct = cts.Token;

        IsLoading = true;
        try
        {
            TimeNodeSnapshot snapshot = await _timeNodeService.GetSnapshotAsync(CurrentGameId.GameBiz, forceRefresh, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            // x:Bind 集合须在 UI 线程更新
            Sections.Clear();
            foreach (TimeNodeSection section in snapshot.Sections)
            {
                var views = section.Items.Select(TimeNodeItemView.FromModel).ToList();
                Sections.Add(new TimeNodeSectionView
                {
                    Title = section.Title,
                    Items = views,
                });
            }
            IsEmpty = !HasTimelyItem(DateTimeOffset.Now);
            ErrorMessage = null;
            if (updateVisibility)
            {
                // 没有任何内节点就完全收起入口，而不是留一个空壳按钮
                this.Visibility = IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            }
            RefreshAllCountdowns();
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            // 只展示的功能：失败就记日志 + 一行提示；探测失败时保持收起，不露出点开也没内容的入口
            _logger.LogWarning(ex, "Load time node failed (biz {Biz})", CurrentGameId.GameBiz.Value);
            ErrorMessage = Lang.Common_Failed;
            IsEmpty = !HasTimelyItem(DateTimeOffset.Now);
            if (updateVisibility)
            {
                this.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            // 只有被新一次加载取代时才不复位（进度环交给新加载）；其余情况含取消都要复位
            if (_loadCts is null || ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
    }


    private void StartCountdownTimer()
    {
        StopCountdownTimer();
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
        _countdownTimer = queue.CreateTimer();
        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.IsRepeating = true;
        _countdownTimer.Tick += (_, _) => RefreshAllCountdowns();
        _countdownTimer.Start();
    }


    private void StopCountdownTimer()
    {
        if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer = null;
        }
    }


    private void RefreshAllCountdowns()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        foreach (TimeNodeSectionView section in Sections)
        {
            foreach (TimeNodeItemView item in section.Items)
            {
                item.RefreshCountdown(now);
            }
        }
    }


    private void CancelLoad()
    {
        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch
        {
            // ignore
        }
        _loadCts = null;
    }


    /// <summary>
    /// 可点击条目悬停：强调色描边 + 略提亮背景。
    /// </summary>
    private void Item_Border_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not TimeNodeItemView { HasLink: true })
        {
            return;
        }
        // 只改 Brush，不改 BorderThickness，避免布局抖动
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? accentObj) && accentObj is Brush accent)
        {
            border.BorderBrush = accent;
        }
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out object? bgObj) && bgObj is Brush bg)
        {
            border.Background = bg;
        }
    }


    /// <summary>
    /// 离开可点击条目：恢复默认描边与背景（厚度不变）。
    /// </summary>
    private void Item_Border_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? strokeObj) && strokeObj is Brush stroke)
        {
            border.BorderBrush = stroke;
        }
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out object? bgObj) && bgObj is Brush bg)
        {
            border.Background = bg;
        }
    }


    private async void Item_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TimeNodeItemView item } && item.HasLink && Uri.TryCreate(item.LinkUrl, UriKind.Absolute, out Uri? uri))
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Open time node link failed: {Url}", item.LinkUrl);
            }
        }
    }

}
