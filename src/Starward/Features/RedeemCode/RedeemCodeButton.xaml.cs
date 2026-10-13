using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Core.MiyoLive;
using Starward.Features.Setting;
using Starward.Helpers;
using Starward.Language;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.RedeemCode;

/// <summary>
/// 游戏启动页右侧「兑换码」入口：图标按钮 + Flyout 展示前瞻直播码（可复制）。
/// </summary>
/// <remarks>
/// 入口只在「至少有一条兑换码」时显示，否则整块收起（前瞻还没开始 / 已经结束 / 没有活动时都不会露出入口）。
/// 上游用 Lottie 悬浮动画与 InstantTooltip；本分支改为静态 FontIcon，且按钮不带 Tooltip 文案
/// （Lang 里没有「前瞻直播兑换码」这个词条，本次移植不允许新增本地化资源）。
/// </remarks>
[INotifyPropertyChanged]
public sealed partial class RedeemCodeButton : UserControl
{

    private readonly ILogger<RedeemCodeButton> _logger = AppConfig.GetLogger<RedeemCodeButton>();

    private readonly RedeemCodeService _redeemCodeService = AppConfig.GetService<RedeemCodeService>();

    private CancellationTokenSource? _loadCts;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;


    public RedeemCodeButton()
    {
        this.InitializeComponent();
        this.Visibility = Visibility.Collapsed;
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
    }


    /// <summary>
    /// 语言切换后刷新 x:Bind 文案。
    /// </summary>
    private void OnLanguageChanged(object _, LanguageChangedMessage __)
    {
        this.Bindings.Update();
        UpdateEmptyMessage();
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


    public ObservableCollection<RedeemCodeItemView> Codes { get; } = new();


    public bool HasCodes { get; set => SetProperty(ref field, value); }


    public bool IsLoading { get; set => SetProperty(ref field, value); }


    /// <summary>Flyout 标题（活动名）；活动名拿不到时为 null，标题整块收起。</summary>
    public string? DisplayTitle { get; set => SetProperty(ref field, value); }


    /// <summary>空态 / 加载失败文案。</summary>
    public string EmptyMessage { get; set => SetProperty(ref field, value); } = Lang.HoyolabToolboxPage_NoData;


    public bool ShowEmptyMessage { get; set => SetProperty(ref field, value); }


    private bool _loadFailed;


    /// <summary>
    /// 该游戏是否有国服 / bilibili 的直播活动配置；
    /// 上游用 GameFeatureConfig.SupportRedeemCode，本分支不新增那个开关，直接问 MiyoLiveActivityConfig。
    /// </summary>
    private static bool IsSupported(GameBiz gameBiz) => MiyoLiveActivityConfig.FromGameBiz(gameBiz) is not null;


    private void Button_RedeemCode_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeForCurrentGame();
    }


    private void Button_RedeemCode_Unloaded(object sender, RoutedEventArgs e)
    {
        StopRefreshTimer();
        CancelLoad();
        Codes.Clear();
        HasCodes = false;
        ShowEmptyMessage = false;
        DisplayTitle = null;
        _loadFailed = false;
        this.Visibility = Visibility.Collapsed;
    }


    /// <summary>
    /// 解析当前游戏并探测入口是否该出现。
    /// </summary>
    /// <remarks>
    /// 这里会走一次 miyolive 请求（与打开弹层走的是同一份 2 分钟内存缓存，所以随后打开弹层不会重复请求）。
    /// 这是「没有兑换码就隐藏入口」这条要求的必要代价：没有数据就无从判断。
    /// 只在页面加载 / 切换游戏时各探测一次，不轮询；60 秒的静默刷新只在弹层打开期间存在。
    /// </remarks>
    private async void InitializeForCurrentGame()
    {
        if (CurrentGameId is null || !IsSupported(CurrentGameId.GameBiz))
        {
            this.Visibility = Visibility.Collapsed;
            StopRefreshTimer();
            Codes.Clear();
            HasCodes = false;
            ShowEmptyMessage = false;
            DisplayTitle = null;
            return;
        }
        await LoadSnapshotAsync(forceRefresh: false, updateVisibility: true);
    }


    private async void Flyout_RedeemCode_Opened(object sender, object e)
    {
        // 定时器必须在 await 之前起：弹层若在加载期间被关闭，Closed 会先于续体执行，
        // 那时 StopRefreshTimer 停不到还没创建的定时器，续体再起一个就成了关不掉的后台轮询
        StartRefreshTimer();
        await LoadSnapshotAsync(forceRefresh: false, updateVisibility: false);
    }


    private void Flyout_RedeemCode_Closed(object sender, object e)
    {
        StopRefreshTimer();
        CancelLoad();
        // 打开期间码被撤下（或刷新后一条都不剩）时，入口要跟着收起
        if (!HasCodes)
        {
            this.Visibility = Visibility.Collapsed;
        }
    }


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
            RedeemCodeSnapshot snapshot = await _redeemCodeService.GetSnapshotAsync(CurrentGameId.GameBiz, forceRefresh, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            ApplySnapshot(snapshot);
            if (updateVisibility)
            {
                // 至少有一条码才露出入口，否则完全收起
                this.Visibility = HasCodes ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            // 本功能仅展示：API / 网络失败不显示红字，只记日志并提示「加载失败」
            _logger.LogWarning(ex, "Load redeem code failed (biz {Biz})", CurrentGameId.GameBiz.Value);
            ShowLoadFailed();
            if (updateVisibility)
            {
                this.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            // 只有被新一次加载取代时才不复位（进度环交给新加载）；其余情况含取消都要复位，
            // 否则 IsLoading 会一直是 true，挡住 60 秒定时刷新里的 !IsLoading 判断
            if (_loadCts is null || ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
    }


    private void ApplySnapshot(RedeemCodeSnapshot snapshot)
    {
        if (snapshot.LoadFailed)
        {
            ShowLoadFailed();
            return;
        }

        DisplayTitle = string.IsNullOrWhiteSpace(snapshot.Title) ? null : snapshot.Title;
        _loadFailed = false;

        Codes.Clear();
        foreach (RedeemCodeItem item in snapshot.Codes)
        {
            Codes.Add(RedeemCodeItemView.FromModel(item));
        }

        HasCodes = Codes.Count > 0;
        UpdateEmptyMessage();
    }


    /// <summary>
    /// 标记本次加载失败：已有码时保留列表，只在没有内容时提示「加载失败」。
    /// </summary>
    private void ShowLoadFailed()
    {
        _loadFailed = true;
        HasCodes = Codes.Count > 0;
        UpdateEmptyMessage();
    }


    private void UpdateEmptyMessage()
    {
        if (HasCodes)
        {
            ShowEmptyMessage = false;
            return;
        }

        ShowEmptyMessage = true;
        // 上游有「加载失败 / 前瞻直播尚未开始 / 暂无直播兑换码」三条文案；
        // 本分支只保留「失败」与「没有数据」两条（未开播时入口本来就是收起的）。
        EmptyMessage = _loadFailed ? Lang.Common_Failed : Lang.HoyolabToolboxPage_NoData;
    }


    private void StartRefreshTimer()
    {
        StopRefreshTimer();
        // 直播掉码：打开期间约每 60s 静默刷新（关掉弹层即停，不做常驻轮询）
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _refreshTimer = queue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(60);
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += async (_, _) =>
        {
            if (!IsLoading)
            {
                await LoadSnapshotAsync(forceRefresh: true, updateVisibility: false);
            }
        };
        _refreshTimer.Start();
    }


    private void StopRefreshTimer()
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer = null;
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


    private void Item_Border_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? accentObj) && accentObj is Brush accent)
        {
            border.BorderBrush = accent;
        }
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out object? bgObj) && bgObj is Brush bg)
        {
            border.Background = bg;
        }
    }


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
        if (sender is not FrameworkElement { Tag: RedeemCodeItemView item } || string.IsNullOrWhiteSpace(item.Code))
        {
            return;
        }

        ClipboardHelper.SetText(item.Code);
        // 右侧图标：复制 → 对勾约 1 秒（对齐抽卡页复制 URL 反馈）
        item.IsCopied = true;
        try
        {
            await Task.Delay(1000);
        }
        catch
        {
            // ignore
        }
        item.IsCopied = false;
    }

}
