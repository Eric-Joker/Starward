using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.HoYoPlay;
using Starward.Features.Setting;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation;


namespace Starward.Features.GameSelector;

[INotifyPropertyChanged]
public sealed partial class GameSelector : UserControl
{


    public event EventHandler<(GameId, bool DoubleTapped)>? CurrentGameChanged;


    private readonly HoYoPlayService _hoyoplayService = AppConfig.GetService<HoYoPlayService>();


    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();



    /// <summary>
    /// 每张大图 DataTemplate 里的「选择服务器」遮罩两个元素：
    /// <c>Grid_GameBizDisplay_Logo</c> 与 <c>Grid_GameBizDisplay_IconAndName</c>。
    /// 模板里的 x:Name 不生成字段，第一次用到时按名字取一次并缓存，避免每次指针移动都遍历视觉树。
    /// </summary>
    private sealed class PosterOverlay
    {
        public FrameworkElement? Logo;
        public FrameworkElement? IconAndName;
    }


    private readonly Dictionary<Grid, PosterOverlay> _posterOverlayCache = new();



    /// <summary>
    /// 「点别处关预设浮窗」挂在窗口内容根（<c>XamlRoot.Content</c>）上，这里留着它用来退订：
    /// 控件卸载时必须摘掉，否则会留下一条指向本控件的订阅。
    /// </summary>
    private UIElement? _windowContent;



    public GameSelector()
    {
        this.InitializeComponent();
        _hideServerListTimer = DispatcherQueue.CreateTimer();
        _hideServerListTimer.Interval = TimeSpan.FromMilliseconds(350);
        _hideServerListTimer.IsRepeating = false;
        _hideServerListTimer.Tick += (_, _) => HideServerListIfPointerLeft();
        InitializeGameSelector();
        this.Loaded += GameSelector_Loaded;
        this.Unloaded += GameSelector_Unloaded;
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<MainWindowDragRectAdaptToGameIconMessage>(this, OnMainWindowStateChanged);
    }



    public GameBiz CurrentGameBiz { get; set; }


    public GameId? CurrentGameId { get; set; }


    public ObservableCollection<GameBizIcon> GameBizIcons { get; set => SetProperty(ref field, value); } = new();


    public GameBizIcon? CurrentGameBizIcon { get; set => SetProperty(ref field, value); }


    public bool IsPinned { get; set => SetProperty(ref field, value); }


    private bool ignoreDpiChanged = false;

    private double lastScale = 1;


    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideServerListTimer;


    /// <summary>
    /// 鼠标当前是否停在预设浮窗上。
    /// </summary>
    private bool _pointerOnLaunchProfile;


    /// <summary>
    /// 鼠标当前是否停在区服列表弹出层上。
    /// </summary>
    private bool _pointerOnServerList;


    /// <summary>
    /// 鼠标现在停在哪张大图上。换到大图 B 时可能先收到 B 的 PointerEntered 再收到 A 的
    /// PointerExited（顺序不固定），据此分清「鼠标真的移开了」和「鼠标已经到另一张大图上」。
    /// </summary>
    private Grid? _hoveredPosterTile;


    /// <summary>
    /// 指针最近一次的坐标（本控件坐标系）。悬停时每次指针事件都刷新，移开后清空。
    /// 收起计时器到点时用它确认「鼠标确实不在大图上」，避开平台把指针交给弹出层时
    /// 大图那次没有鼠标移出、坐标却还停在大图里的 PointerExited。
    /// </summary>
    private Point? _cachedPointerPosition;


    /// <summary>
    /// 当前展开了区服列表的大图。为 null 时页面上没有区服列表。
    /// </summary>
    private Grid? _serverListAnchor;


    /// <summary>
    /// 当前打开着的区服列表（<c>Border_Servers</c>，窗口树里唯一个一个共用面板，
    /// 内容按当前大图切换）。它是「完整选择器」唯一的状态来源：它可见时区服项才可能被指到。
    /// <para>
    /// 它是窗口树里的普通面板，不是 Popup：只有留在窗口视觉树里 <c>AcrylicBrush</c> 才能
    /// 正常取到背景采样（挂在 Popup 的独立合成层里会退回 α≈96% 的 FallbackColor，看起来实心）。
    /// 面板自己不参与开关，开 / 收只有本类一个主人。
    /// </para>
    /// </summary>


    /// <summary>
    /// 当前打开着的区服列表里的 <c>ItemsControl_Servers</c>。
    /// </summary>
    private FrameworkElement? _serverListItems;


    /// <summary>
    /// 当前显示着启动预设浮窗的区服。为 null 时不显示。
    /// </summary>
    private GameBiz? _launchProfileGameBiz;


    /// <summary>
    /// 预设浮窗贴着哪个元素显示。
    /// </summary>
    private FrameworkElement? _launchProfileAnchor;


    /// <summary>
    /// 预设浮窗的锚点是不是「完整选择器」里区服列表的某一行。
    /// 决定面板贴着行的右侧还是贴在大图下边缘，也决定收起区服列表时要不要连它一起收。
    /// </summary>
    private bool _launchProfileOnServerRow;


    /// <summary>
    /// 正在拖动预设排序：拖动期间不要因为鼠标离开而收起浮窗。
    /// </summary>
    private bool _launchProfileDragging;



    public void InitializeGameSelector()
    {
        List<GameInfo> gameInfos = GameLauncherService.GetCachedGameInfos();
        InitializeGameIconsArea(gameInfos);
        InitializeGameServerArea(gameInfos);
        InitializeInstalledGamesCommand.Execute(null);
    }



    private async void GameSelector_Loaded(object sender, RoutedEventArgs e)
    {
        this.XamlRoot.Changed -= XamlRoot_Changed;
        this.XamlRoot.Changed += XamlRoot_Changed;
        AttachWindowContentPointerPressed();
        await Task.Delay(1000);
        await UpdateGameInfoAsync();
    }



    /// <summary>
    /// 卸载时把挂在窗口内容根上的按下事件摘掉，避免订阅泄漏（这个控件会被反复加载 / 卸载）。
    /// </summary>
    private void GameSelector_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachWindowContentPointerPressed();
        // 卸载时这个控件已经在视觉树之外，XamlRoot 可能已经被清成 null（实测在关闭窗口时必现），
        // 直接点它会 NullReferenceException。
        if (this.XamlRoot is XamlRoot xamlRoot)
        {
            xamlRoot.Changed -= XamlRoot_Changed;
        }
    }



    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // 换了 XamlRoot（窗口内容根被换掉）时把按下事件挪到新的内容根上
        AttachWindowContentPointerPressed();
        if (ignoreDpiChanged)
        {
            return;
        }
        if (lastScale != sender.RasterizationScale)
        {
            lastScale = sender.RasterizationScale;
            UpdateDragRectangles();
        }
    }



    /// <summary>
    /// 把「点别处关预设浮窗」挂到窗口内容根上。已经在同一根上时什么都不做，所以重复调用安全。
    /// </summary>
    private void AttachWindowContentPointerPressed()
    {
        try
        {
            UIElement? content = this.XamlRoot?.Content;
            if (content is null || ReferenceEquals(_windowContent, content))
            {
                return;
            }
            DetachWindowContentPointerPressed();
            _windowContent = content;
            _windowContent.PointerPressed += WindowContent_PointerPressed;
        }
        catch { }
    }



    /// <summary>
    /// 摘掉窗口内容根上的按下事件。保留 <see cref="_windowContent"/> 的引用是为了在
    /// <c>XamlRoot.Changed</c> 里比对、以及卸载时能准确退订。
    /// </summary>
    private void DetachWindowContentPointerPressed()
    {
        try
        {
            if (_windowContent is not null)
            {
                _windowContent.PointerPressed -= WindowContent_PointerPressed;
            }
        }
        catch { }
    }





    private async void OnLanguageChanged(object? _, LanguageChangedMessage __)
    {
        this.Bindings.Update();
        await UpdateGameInfoAsync();
    }




    private async void OnMainWindowStateChanged(object? _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate && (message.ElapsedOver(TimeSpan.FromMinutes(10)) || message.IsCrossingHour))
            {
                await UpdateGameInfoAsync();
            }
        }
        catch { }
    }



    private void OnMainWindowStateChanged(object? _, MainWindowDragRectAdaptToGameIconMessage message)
    {
        if (!message.IgnoreDpiChanged)
        {
            UpdateDragRectangles();
        }
        ignoreDpiChanged = message.IgnoreDpiChanged;
    }







    #region Game Icon


    /// <summary>
    /// 初始化游戏图标区域
    /// </summary>
    private void InitializeGameIconsArea(List<GameInfo> gameInfos)
    {
        try
        {
            GameBizIcons.CollectionChanged -= GameBizIcons_CollectionChanged;
            GameBizIcons.Clear();

            // 从配置文件读取已选的 GameBiz
            string? bizs = AppConfig.SelectedGameBizs;
            foreach (string str in bizs?.Split(',')?.Distinct() ?? [])
            {
                if (GameBiz.TryParse(str, out GameBiz biz))
                {
                    // 已知的 GameBiz
                    GameBizIcons.Add(new GameBizIcon(biz));
                }
                else if (gameInfos.FirstOrDefault(x => x.GameBiz == biz) is GameInfo info)
                {
                    // 由 HoYoPlay API 获取，但未适配的 GameBiz
                    GameBizIcons.Add(new GameBizIcon(info));
                }
            }

            // 选取当前游戏
            GameBiz lastSelectedGameBiz = AppConfig.CurrentGameBiz;
            if (GameBizIcons.FirstOrDefault(x => x.GameBiz == lastSelectedGameBiz) is GameBizIcon icon)
            {
                CurrentGameBizIcon = icon;
                CurrentGameBizIcon.IsSelected = true;
                CurrentGameBiz = lastSelectedGameBiz;
            }
            else if (lastSelectedGameBiz.IsKnown())
            {
                CurrentGameBizIcon = new GameBizIcon(lastSelectedGameBiz);
                CurrentGameBizIcon.IsSelected = true;
                CurrentGameBiz = lastSelectedGameBiz;
            }
            else if (gameInfos.FirstOrDefault(x => x.GameBiz == lastSelectedGameBiz) is GameInfo info)
            {
                CurrentGameBizIcon = new GameBizIcon(info);
                CurrentGameBizIcon.IsSelected = true;
                CurrentGameBiz = lastSelectedGameBiz;
            }

            CurrentGameId = CurrentGameBizIcon?.GameId;
            if (CurrentGameId is not null)
            {
                CurrentGameChanged?.Invoke(this, (CurrentGameId, false));
            }

            if (AppConfig.IsGameBizSelectorPinned)
            {
                Pin();
            }

            GameBizIcons.CollectionChanged += GameBizIcons_CollectionChanged;
        }
        catch { }
        if (GameBizIcons.Count == 0 && CurrentGameBizIcon is null)
        {
            TeachTip_SelectGame.IsOpen = true;
        }
    }



    /// <summary>
    /// 游戏图标区域是否可见
    /// </summary>
    public bool GameIconsAreaVisible
    {
        get => Grid_GameIconsArea.Translation == Vector3.Zero;
        set
        {
            if (value)
            {
                Grid_GameIconsArea.Translation = Vector3.Zero;
            }
            else
            {
                Grid_GameIconsArea.Translation = new Vector3(0, -100, 0);
            }
            UpdateDragRectangles();
        }
    }


    /// <summary>
    /// 更新窗口拖拽区域
    /// </summary>
    public void UpdateDragRectangles()
    {
        try
        {
            double x = Border_CurrentGameIcon.ActualWidth;
            if (GameIconsAreaVisible)
            {
                x = Border_CurrentGameIcon.ActualWidth + Grid_GameIconsArea.ActualWidth;
            }
            this.XamlRoot.SetWindowDragRectangles([new Rect(x, 0, 10000, 48)]);
        }
        catch { }
    }



    /// <summary>
    /// 鼠标移入到当前游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Border_CurrentGameIcon_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // 显示所有游戏图标
        GameIconsAreaVisible = true;
    }



    /// <summary>
    /// 鼠标移出当前游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Border_CurrentGameIcon_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (FullBackgroundVisible || IsPinned)
        {
            // 当前游戏图标被固定或者全屏显示时，不隐藏所有游戏图标
            return;
        }
        if (sender is UIElement ele)
        {
            var postion = e.GetCurrentPoint(sender as UIElement).Position;
            if (postion.X > ele.ActualSize.X - 1 && postion.Y > 0 && postion.Y < ele.ActualSize.Y)
            {
                // 从右侧移出，此时进入到所有游戏图标区域，不隐藏
                return;
            }
        }
        // 其他方向移出，隐藏所有游戏图标
        GameIconsAreaVisible = false;
    }



    /// <summary>
    /// 鼠标移出所有游戏图标区域
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIconsArea_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (FullBackgroundVisible || IsPinned)
        {
            return;
        }
        GameIconsAreaVisible = false;
    }



    /// <summary>
    /// 点击没有被选择的游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (CurrentGameBizIcon is not null)
            {
                CurrentGameBizIcon.IsSelected = false;
            }

            CurrentGameBizIcon = icon;
            CurrentGameBiz = icon.GameBiz;
            CurrentGameId = icon.GameId;
            icon.IsSelected = true;

            CurrentGameChanged?.Invoke(this, (icon.GameId, false));
            AppConfig.CurrentGameBiz = icon.GameBiz;
        }
    }



    /// <summary>
    /// 双击没有被选择的游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (CurrentGameBizIcon is not null)
            {
                CurrentGameBizIcon.IsSelected = false;
            }

            CurrentGameBizIcon = icon;
            CurrentGameBiz = icon.GameBiz;
            CurrentGameId = icon.GameId;
            icon.IsSelected = true;
            HideFullBackground();

            CurrentGameChanged?.Invoke(this, (icon.GameId, true));
            AppConfig.CurrentGameBiz = icon.GameBiz;
        }
    }


    /// <summary>
    /// 鼠标移入到游戏图标：只做图标自己的高亮。
    /// 预设浮窗不在这里弹：产品要求标准选择器里只有右键 / 长按才显示，悬停不显示。
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (!icon.IsSelected)
            {
                icon.MaskOpacity = 0;
            }
        }
    }



    /// <summary>
    /// 鼠标移出游戏图标：只还原图标自己的高亮。
    /// 悬停不再弹预设浮窗，所以这里不收任何东西：右键 / 长按弹出的那个面板
    /// 由它自己的指针进出负责收起（<see cref="LaunchProfileFlyout_PointerExited"/>），
    /// 在这里启动收起计时反而会把「从图标挪到面板上」那一下打断。
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (!icon.IsSelected)
            {
                icon.MaskOpacity = 1;
            }
        }
    }




    /// <summary>
    /// 右键游戏图标：弹出该区服的启动预设浮窗。
    /// </summary>
    private void Grid_GameIcon_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            e.Handled = true;
            ShowLaunchProfileFlyout(icon.GameBiz, fe);
        }
    }



    /// <summary>
    /// 长按游戏图标（触摸 / 触笔），等同于右键。
    /// </summary>
    private void Grid_GameIcon_Holding(object sender, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs e)
    {
        if (e.HoldingState is Microsoft.UI.Input.HoldingState.Started
            && sender is FrameworkElement fe
            && fe.DataContext is GameBizIcon icon)
        {
            e.Handled = true;
            ShowLaunchProfileFlyout(icon.GameBiz, fe);
        }
    }



    /// <summary>
    /// 游戏图标区域大小变化
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIconsArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDragRectangles();
    }



    /// <summary>
    /// 固定游戏待选区
    /// </summary>
    [RelayCommand]
    private void Pin()
    {
        IsPinned = !IsPinned;
        if (IsPinned)
        {
            GameIconsAreaVisible = true;
        }
        else
        {
            // 避免在固定时更换当前游戏，取消固定后，左上角的图标不改变的问题
            var temp = CurrentGameBizIcon;
            CurrentGameBizIcon = null;
            CurrentGameBizIcon = temp;
            if (!FullBackgroundVisible)
            {
                GameIconsAreaVisible = false;
            }
        }
        AppConfig.IsGameBizSelectorPinned = IsPinned;
    }



    /// <summary>
    /// 待选游戏变更时，保存配置
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void GameBizIcons_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var icon in GameBizIcons)
            {
                sb.Append(icon.GameBiz);
                sb.Append(',');
            }
            AppConfig.SelectedGameBizs = sb.ToString().TrimEnd(',');
        }
        catch { }
    }



    #endregion





    #region 启动预设


    /// <summary>
    /// 切换到某个区服并选中它的某个启动预设。预设选择浮窗里点条目时走这里。
    /// </summary>
    private void SelectGameBizAndLaunchProfile(GameBizIcon server, string profileId)
    {
        try
        {
            // 先把预设校验一遍：内部名不存在时什么都不做
            if (GameLaunchProfileStore.GetProfile(server.GameBiz, profileId) is null)
            {
                return;
            }
            // 切区服时不要顺手选第一个预设，否则会把调用方指定的预设覆盖掉，
            // 浮窗里的「当前预设」高亮就会一直停在第一行
            SelectGameBiz(server, selectFirstProfile: false);
            GameLaunchProfileStore.SetCurrentProfile(server.GameBiz, profileId);
            // 存储写完之后广播一次，和 GameLaunchProfileWindow 关闭时发的是同一个消息：
            // 启动器页面收到后重读当前预设名（见 GameLauncherPage.OnGameLaunchProfileChanged）。
            // 必须在 CurrentGameChanged 之前发：那边会切页面，页面在切换之前读的是
            // 它自己那份 CurrentGameBiz；而这里已经通过 SelectGameBiz 把当前区服写进 AppConfig 了，
            // 所以「切区服 + 换预设」和「同一个区服内换预设」两种情况下读到的都是这份新预设。
            WeakReferenceMessenger.Default.Send(new GameLaunchProfileChangedMessage());
            CurrentGameChanged?.Invoke(this, (server.GameId, false));
        }
        catch { }
        finally
        {
            HideLaunchProfileFlyout();
        }
    }



    /// <summary>
    /// 选中某个区服。点击区服项时走这里，并使用该区服的第一个启动预设；
    /// 由预设浮窗切换时传 <paramref name="selectFirstProfile"/> = false，保留调用方已经选好的预设。
    /// </summary>
    private void SelectGameBiz(GameBizIcon server, bool selectFirstProfile = true)
    {
        if (selectFirstProfile)
        {
            string profileId = GameLaunchProfileStore.GetFirstProfile(server.GameBiz).Id;
            GameLaunchProfileStore.SetCurrentProfile(server.GameBiz, profileId);
        }
        GameBizIcon target = server;
        if (GameBizIcons.FirstOrDefault(x => x.GameId == server.GameId) is GameBizIcon pinned)
        {
            target = pinned;
            server.IsSelected = false;
        }
        if (CurrentGameBizIcon is not null && CurrentGameBizIcon != target)
        {
            CurrentGameBizIcon.IsSelected = false;
        }
        CurrentGameBizIcon = target;
        CurrentGameBiz = target.GameBiz;
        CurrentGameId = target.GameId;
        target.IsSelected = true;
        AppConfig.CurrentGameBiz = target.GameBiz;
    }



    /// <summary>
    /// 弹出预设选择浮窗。只有两条路径会走到这里：完整选择器里悬停区服项，
    /// 以及标准选择器里右键 / 长按游戏图标（悬停图标不再弹）。
    /// </summary>
    /// <param name="gameBiz">浮窗显示哪个区服的预设。</param>
    /// <param name="anchor">浮窗贴着哪个元素显示。区服项时传那一行本身，浮窗就贴在该行右边。</param>
    private void ShowLaunchProfileFlyout(GameBiz gameBiz, FrameworkElement anchor)
    {
        try
        {
            _hideServerListTimer.Stop();
            // 锚点是区服列表里的区服项时，列表必须留着（面板就贴在这一行右边）。
            // 锚点在别处（右键 / 长按游戏图标）时，先把页面上还开着的区服列表收起来，
            // 避免两个弹出层同时留着。
            _launchProfileOnServerRow = IsServerListAnchorElement(anchor);
            if (!_launchProfileOnServerRow)
            {
                CloseServerListAndLaunchProfile();
            }
            _launchProfileGameBiz = gameBiz;
            _launchProfileAnchor = anchor;
            LaunchProfileFlyout.Mode = GameLaunchProfileFlyout.DisplayMode.Panel;
            LaunchProfileFlyout.Refresh(gameBiz, AppConfig.GetCurrentLaunchProfileId(gameBiz));
            // 先把它挪到窗口外再显示：定位要用命中测试量锚点，面板自己压在列表上会把量出来的
            // 边界截断（这一次的 Margin 还是上一次留下的位置），挪开再量就不受影响
            LaunchProfileFlyout.Margin = new Thickness(-100000, -100000, 0, 0);
            LaunchProfileFlyout.Visibility = Visibility.Visible;
            RefreshLaunchProfileHighlight();
            PositionLaunchProfileFlyout();
        }
        catch { }
    }



    private void HideLaunchProfileFlyout()
    {
        _launchProfileGameBiz = null;
        _launchProfileAnchor = null;
        _launchProfileOnServerRow = false;
        _launchProfileDragging = false;
        _pointerOnLaunchProfile = false;
        LaunchProfileFlyout.Visibility = Visibility.Collapsed;
        RefreshLaunchProfileHighlight();
    }



    /// <summary>
    /// 鼠标离开大图 / 区服项 / 预设浮窗：延迟收起，给用户挪到另一个上面的时间。
    /// 真正移进去的一方会调用 <see cref="CancelHideServerList"/> 取消这次收起。
    /// </summary>
    private void StartHideServerListTimer()
    {
        _hideServerListTimer.Stop();
        _hideServerListTimer.Start();
    }



    /// <summary>
    /// 取消延迟收起，并把「鼠标还停在其中的某一个上面」记下来。
    /// 计时器到点时只按这里记下的状态判断，不再做几何命中判断。
    /// </summary>
    /// <param name="onServerList">这一次是不是停在区服列表上。</param>
    /// <param name="onLaunchProfile">这一次是不是停在预设浮窗上。</param>
    private void CancelHideServerList(bool? onServerList = null, bool? onLaunchProfile = null)
    {
        _hideServerListTimer.Stop();
        if (onServerList is bool serverList)
        {
            _pointerOnServerList = serverList;
        }
        if (onLaunchProfile is bool launchProfile)
        {
            _pointerOnLaunchProfile = launchProfile;
        }
    }



    /// <summary>
    /// 刷新缓存的指针坐标（本控件坐标系）。取不到时保留上一次的坐标。
    /// </summary>
    private void UpdateCachedPointerPosition(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            _cachedPointerPosition = e.GetCurrentPoint(null).Position;
        }
        catch { }
    }



    /// <summary>
    /// 收起区服列表和预设浮窗的唯一入口：悬停计时器到点、点击区服项、点黑色背景、换大图都走这里。
    /// 列表的开关只有这一处决定（面板没有自己的关闭行为），所以不需要取消 Closing、重开抑制这些补丁。
    /// 预设浮窗的锚点在这个列表里时把它一起收掉，避免留下一个悬在半空的浮窗；
    /// 锚点在别处（右键 / 长按游戏图标）时不动它，它有自己的收起计时。
    /// </summary>
    private void CloseServerListAndLaunchProfile()
    {
        // 锚点在哪张大图的列表里都算「预设浮窗挂在区服列表上」，不能只看当前这一份：
        // 鼠标从 A 的区服项直接扫到 B 上时，锚点还在 A 的列表里
        if (IsServerListAnchorElement(_launchProfileAnchor))
        {
            HideLaunchProfileFlyout();
        }
        Border_Servers.Visibility = Visibility.Collapsed;
        // 列表收掉了，「选择服务器」遮罩也跟着收（这是移出列表且不在大图上时才走到的路径）
        if (_serverListAnchor is Grid tile)
        {
            RestorePosterOverlay(tile);
        }
        _serverListItems = null;
        _serverListAnchor = null;
        _pointerOnServerList = false;
    }



    /// <summary>
    /// 某个元素是不是某张大图区服列表里的区服项（<c>ItemsControl_Servers</c> 子树里的元素）。
    /// 不依赖「当前开着哪一份列表」：从元素往上找，遇到叫这个名字的 <see cref="ItemsControl"/> 就认。
    /// 「指针是不是停在区服列表上」也用它判断，所以弹出层的内边距环也算在列表里。
    /// </summary>
    private static bool IsServerListAnchorElement(DependencyObject? element)
    {
        DependencyObject? node = element;
        while (node is not null)
        {
            if (node is ItemsControl items && items.Name is "ItemsControl_Servers")
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }



    /// <summary>
    /// 延迟结束后统一判断：鼠标既不在预设浮窗上、也不在区服列表上、也不在大图上，才把它们收起来。
    /// <para>
    /// 判定看「指针现在到底在谁身上」这三条证据：区服列表 / 预设浮窗的指针进出事件、
    /// 缓存的指针坐标落在不在大图或列表的矩形里、以及指针是不是正落在大图和列表之间那条
    /// <see cref="ServerListBelowPosterGap"/> 带里。最后一条是给「大图 → 列表」这段移动兜底的：
    /// 经过那条带时大图已经 <c>PointerExited</c>、列表还没 <c>PointerEntered</c>，
    /// 只看前两条会把它当成已经离开。带的位置用大图与列表各自的实际矩形算，没有另写死坐标。
    /// </para>
    /// </summary>
    private void HideServerListIfPointerLeft()
    {
        if (_pointerOnLaunchProfile || _pointerOnServerList || _launchProfileDragging)
        {
            return;
        }
        if (_cachedPointerPosition is Point point
            && (IsPointerOnPoster(_serverListAnchor, point)
                || IsPointerOnServerList(point)
                || IsPointerInServerListGap(point)))
        {
            // 鼠标没有真的移开：在大图里 / 停在弹出层的内边距环上 / 正经过大图到列表之间那条缝隙
            return;
        }
        CloseServerListAndLaunchProfile();
    }



    /// <summary>
    /// 指针是不是正落在「大图下边缘到区服列表上边缘」这条缝隙带里。
    /// 用发布层与列表各自的实际矩形（本控件坐标系）算，不写死坐标：
    /// 竖直方向在这两张矩形之间（含两端）、水平方向和这两张矩形的并集有交集就算在带里。
    /// 指针真的移到别处时既不满足竖直区间、也不满足水平交集，所以不会误判成「还在悬停」。
    /// </summary>
    private bool IsPointerInServerListGap(Point position)
    {
        if (_serverListAnchor is not Grid tile || _serverListItems is not FrameworkElement list)
        {
            return false;
        }
        if (TransformToGameSelector(tile) is not Point tilePoint
            || TransformToGameSelector(list) is not Point listPoint)
        {
            // 换算出不来时不猜：交给另外两条判定
            return false;
        }
        double tileBottom = tilePoint.Y + tile.ActualHeight;
        double listTop = listPoint.Y;
        double tileLeft = tilePoint.X;
        double tileRight = tileLeft + tile.ActualWidth;
        double listLeft = listPoint.X;
        double listRight = listLeft + list.ActualWidth;
        // 竖直：落在大图下边缘和列表上边缘之间（含边界，留 1px 容差）
        bool betweenRows = position.Y >= tileBottom - 1 && position.Y <= listTop + 1;
        // 水平：和这两张矩形的水平并集有交集
        bool betweenColumns = position.X >= Math.Min(tileLeft, listLeft) - 1
                              && position.X <= Math.Max(tileRight, listRight) + 1;
        return betweenRows && betweenColumns;
    }



    /// <summary>
    /// 预设浮窗和锚点之间留的空隙。
    /// </summary>
    private const double LaunchProfileFlyoutGap = 6;



    /// <summary>
    /// 区服列表贴在大图下边缘时往下让出的间距，和 <see cref="LaunchProfileFlyoutGap"/> 同一个数：
    /// 同一层弹出元素之间保持一致的视觉间距。
    /// </summary>
    private const double ServerListBelowPosterGap = LaunchProfileFlyoutGap;



    /// <summary>
    /// 预设浮窗的外框内边距，和 <c>GameLaunchProfileFlyout.xaml</c> 里的 <c>PanelPadding</c> 同源（当前 6，
    /// 见该文件第 15 行的资源定义，用在第 90 / 178 行的两个面板上）。
    /// 行锚定的语义是「浮窗里第一行的顶边 == 被悬停区服行的顶边」，
    /// 而浮窗顶边在它上方一个内边距处，所以摆放时整块往上让这个数。
    /// 那个资源改了这里要跟着改。
    /// </summary>
    private const double LaunchProfilePanelPadding = 6;



    /// <summary>
    /// 区服列表的内容尺寸变化（区服数量、文字换行）后重新算位置。
    /// </summary>
    private void Border_Servers_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 尺寸刚变完这一刻算出来才准，排到下一个消息循环会让列表先出现在错的位置再跳一下
        PositionServerListPanel();
    }



    /// <summary>
    /// 区服列表相对大图定位置：放在大图<b>下方</b>（下边缘再让 <see cref="ServerListBelowPosterGap"/>），
    /// 并相对大图<b>水平居中</b>（列表中心线 = 大图中心线）。
    /// <para>
    /// 面板和预设浮窗一样是窗口树里的普通元素，所以直接算 <c>Margin</c>（左上角对齐、
    /// 由 Margin 摆放），不用 <c>PlacementTarget</c> / <c>HorizontalOffset</c> / <c>VerticalOffset</c>。
    /// 坐标来源是既有的 <see cref="TransformToGameSelector"/>：把大图换算到本控件坐标系，
    /// 和预设浮窗用的是同一套换算工具、同一个坐标系。
    /// </para>
    /// <para>
    /// 宽度用外框自己的 <c>ActualWidth</c>（= 内容 + 两边 <c>GameServerListPadding</c>），
    /// 它就是列表在水平方向上的实际范围，这个值在窗口树里是可靠的。
    /// 宽高还没量出来时先只贴左边（居中项为 0），<c>SizeChanged</c> 一到就重算。
    /// </para>
    /// </summary>
    private void PositionServerListPanel()
    {
        try
        {
            if (Border_Servers.Visibility is not Visibility.Visible || _serverListAnchor is not Grid tile)
            {
                return;
            }
            if (TransformToGameSelector(tile) is not Point tilePoint)
            {
                // 换算不出来时不猜位置：先收起，免得停在错地方
                Border_Servers.Visibility = Visibility.Collapsed;
                return;
            }
            double listWidth = Border_Servers.ActualWidth;
            double centered = listWidth > 0 ? (tile.ActualWidth - listWidth) / 2 : 0;
            double x = tilePoint.X + centered;
            // 大图贴着窗口边缘时，"相对大图水平居中"会把列表推到窗口外面去，
            // 被父级裁掉之后看起来就是「悬停这张大图不显示区服列表」（崩三那张大图就在最右边）。
            // 和预设浮窗一样把水平位置钳回窗口范围：左边缘不越过窗口左边，右边缘不越过窗口右边。
            if (listWidth > 0)
            {
                double minX = 0;
                double maxX = double.PositiveInfinity;
                if (GetGameSelectorXamlRoot() is XamlRoot xamlRoot)
                {
                    maxX = Math.Max(xamlRoot.Size.Width, listWidth);
                    if (TransformToGameSelector(xamlRoot.Content) is Point rootOrigin)
                    {
                        // 窗口左右边缘在本控件坐标系里的位置
                        minX = -rootOrigin.X;
                        maxX = xamlRoot.Size.Width - rootOrigin.X;
                    }
                }
                x = Math.Max(Math.Min(x, maxX - listWidth), minX);
            }
            Border_Servers.Margin = new Thickness(x,
                                                  tilePoint.Y + tile.ActualHeight + ServerListBelowPosterGap,
                                                  0,
                                                  0);
        }
        catch { }
    }



    /// <summary>
    /// 点「这个控件之外」的任何地方都收预设浮窗：
    /// 挂在 <c>XamlRoot.Content</c>（窗口内容根）的 <c>PointerPressed</c> 上，所以导航栏、主内容、
    /// 空白处这些不属于 GameSelector 的点击也能收到。
    /// <para>
    /// 判定条件：本次按下的原始元素不在预设浮窗子树里（点面板内部，例如「新增 / 编辑预设」按钮，
    /// 不算外部点击）就关。左上角那颗完整 / 标准切换器按钮<b>也算外部</b>，点它也关 ——
    /// 这里不去拦它的 Command（<c>PointerPressed</c> 不标记已处理），所以它的切换行为照旧。
    /// </para>
    /// <para>
    /// 用 <c>PointerPressed</c> 而不是 <c>Tapped</c>：这一层在窗口内容根上，按下的那一刻就能判定，
    /// 而且不把事件标记成已处理，不会影响图标自己的 Tapped / DoubleTapped / RightTapped / Holding；
    /// 也没有任何否决平台关闭、抑制重开的动作 —— 区服列表的开 / 收仍然只有
    /// <see cref="ShowServerListPanel"/> / <see cref="CloseServerListAndLaunchProfile"/> 两个入口。
    /// </para>
    /// </summary>
    private void WindowContent_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (LaunchProfileFlyout.Visibility is not Visibility.Visible)
            {
                return;
            }
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }
            if (IsDescendantOf(source, LaunchProfileFlyout))
            {
                // 点在面板自己身上：不算外部点击
                return;
            }
            // 其余一律关：包括导航栏 / 主内容 / 空白处，以及左上角那颗完整 / 标准切换器按钮
            // （它的切换行为由 Command 自己完成，这边只是顺手把面板收掉，且发生在 Command 之前）
            HideLaunchProfileFlyout();
        }
        catch { }
    }



    /// <summary>
    /// 把预设浮窗贴在锚点旁边，锚点的位置一律用「锚点元素自己的位置」求：
    /// <list type="bullet">
    /// <item>区服列表里的区服项：浮窗贴在那一行右侧（左边缘 = 行右边缘 + 6），上边缘和该行对齐，
    /// 右侧放不下才翻到左边；</item>
    /// <item>标准选择器里右键 / 长按的游戏图标：浮窗贴在该图标下方、左边对齐。</item>
    /// </list>
    /// 区服项挂在 Popup 里，跨弹出层边界的 <c>TransformToVisual</c> 会给出恒等变换，
    /// 所以那一侧先试直接换算，拿不到就用命中测试把这一行在窗口里的矩形量出来
    /// （见 <see cref="TryGetElementWindowRect"/>），不再用「大图位置 + 常量推算」那种会偏的算法。
    /// 两条路都拿不到位置时直接收起浮窗，绝不停在 (0,0)、也不退回贴在大图右侧。
    /// </summary>
    private void PositionLaunchProfileFlyout()
    {
        try
        {
            if (LaunchProfileFlyout.Visibility is not Visibility.Visible || _launchProfileAnchor is null)
            {
                return;
            }
            // 锚点在窗口里的矩形。方法返回 bool，矩形从 out 参数出来
            if (!TryGetElementWindowRect(_launchProfileAnchor, out Rect anchorRect))
            {
                // 位置解析不出来：宁可先不显示，也不要停在窗口左上角或者贴到大图上
                HideLaunchProfileFlyout();
                return;
            }
            // 本控件在窗口里的原点，用来把窗口坐标换成控件坐标。
            // 换算不出来时不猜原点：猜错就等于面板贴到了别的地方，宁可不显示
            if (TransformToGameSelector(this) is not Point selectorOrigin)
            {
                HideLaunchProfileFlyout();
                return;
            }
            double anchorLeft = anchorRect.X - selectorOrigin.X;
            double anchorTop = anchorRect.Y - selectorOrigin.Y;
            double anchorRight = anchorLeft + anchorRect.Width;
            double anchorBottom = anchorTop + anchorRect.Height;
            double width = LaunchProfileFlyout.ActualWidth;
            double height = LaunchProfileFlyout.ActualHeight;
            // 窗口在本控件坐标系里的范围，钳制用
            double minX = 0;
            double minY = 0;
            double maxX = double.PositiveInfinity;
            double maxY = double.PositiveInfinity;
            if (GetGameSelectorXamlRoot() is XamlRoot xamlRoot)
            {
                maxX = Math.Max(xamlRoot.Size.Width, width);
                maxY = Math.Max(xamlRoot.Size.Height, height);
                if (TransformToGameSelector(xamlRoot.Content) is Point rootOrigin)
                {
                    minX = -rootOrigin.X;
                    minY = -rootOrigin.Y;
                }
            }
            double x;
            double y;
            if (_launchProfileOnServerRow)
            {
                // 贴在区服项右侧，上边缘和它对齐；第一次弹出还没量出宽度时先按右侧放，
                // 等 SizeChanged 拿到宽度再钳制
                double spaceRight = maxX - anchorRight - LaunchProfileFlyoutGap;
                double spaceLeft = anchorLeft - LaunchProfileFlyoutGap - minX;
                if (width <= 0 || spaceRight >= width || spaceRight >= spaceLeft)
                {
                    x = anchorRight + LaunchProfileFlyoutGap;
                }
                else
                {
                    // 悬停的是靠右的区服项，右侧放不下，翻到左侧
                    x = anchorLeft - LaunchProfileFlyoutGap - width;
                }
                // 对齐语义（与已接受的那一版逐字相同的公式）：
                //   浮窗里第一行的顶边 == 被悬停区服行的顶边
                // 浮窗顶边在「第一行顶边上方一个外框内边距」处，所以整块往上让这个内边距
                y = anchorTop - LaunchProfilePanelPadding;
            }
            else
            {
                // 标准选择器：贴在游戏图标下边缘、左边对齐，和普通 Flyout 一样
                // （这里是「整体贴在图标下面」，不需要按第一行去顶）
                x = anchorLeft;
                y = anchorBottom + LaunchProfileFlyoutGap;
            }
            if (width > 0)
            {
                x = Math.Max(Math.Min(x, maxX - width), minX);
            }
            if (height > 0)
            {
                y = Math.Max(Math.Min(y, maxY - height), minY);
            }
            LaunchProfileFlyout.Margin = new Thickness(x, y, 0, 0);
        }
        catch { }
    }



    /// <summary>
    /// 取 <paramref name="element"/> 在窗口内容坐标系里的矩形。
    /// 面板和区服行现在都在窗口视觉树里（不再有弹出层边界），所以直接 <c>TransformToVisual</c>
    /// 换算就行；拿不到时才退回命中测试 <see cref="TryMeasureElementRectByHitTest"/>。
    /// 两条都拿不到时返回 false，调用方据此把浮窗收起来，而不是摆到一个猜出来的位置上。
    /// </summary>
    private bool TryGetElementWindowRect(FrameworkElement element, out Rect rect)
    {
        rect = default;
        if (GetGameSelectorXamlRoot()?.Content is not FrameworkElement rootVisual)
        {
            return false;
        }
        // 一、同树直接换算
        if (TransformToVisualRoot(element, rootVisual) is Point windowPoint)
        {
            // Windows.Foundation.Rect 的四个参数是 float，布局量出来的是 double，
            // 取整到最近的像素再转，别用截断（截断对负坐标会往反方向偏）
            float left = (float)Math.Round(windowPoint.X);
            float top = (float)Math.Round(windowPoint.Y);
            rect = new Rect(left, top,
                            (float)Math.Round(left + element.ActualWidth) - left,
                            (float)Math.Round(top + element.ActualHeight) - top);
            return true;
        }
        // 二、兜底：用命中测试把矩形量出来
        if (TryMeasureElementRectByHitTest(element, rootVisual, out rect))
        {
            return true;
        }
        rect = default;
        return false;
    }



    /// <summary>
    /// 用命中测试量出 <paramref name="element"/> 的矩形：在窗口里按 <c>ScanStep</c> 的步长铺点，
    /// 找到第一个命中这个元素的点，再从它沿四个方向往外走 / 二分逼近四条边。
    /// 元素至少有几十 px，所以这个步长不会漏掉它；全程只用命中测试，不碰 <c>TransformToVisual</c>。
    /// <para>
    /// 边界值全程按 double 算，只在最后构造 <see cref="Rect"/> 时转成 float，
    /// 免得二分过程中因为 float 精度反复抖动。
    /// </para>
    /// </summary>
    private bool TryMeasureElementRectByHitTest(FrameworkElement element, FrameworkElement rootVisual, out Rect rect)
    {
        rect = default;
        const double ScanStep = 16;
        const double WalkStep = 8;
        if (!IsMeasurable(element))
        {
            return false;
        }
        if (FindPointInsideElement(element, rootVisual, ScanStep) is not Point inside)
        {
            return false;
        }
        if (!TryWalkToEdge(element, rootVisual, inside.X, inside.Y, -1, 0, WalkStep, out double left)
            || !TryWalkToEdge(element, rootVisual, inside.X, inside.Y, 1, 0, WalkStep, out double right)
            || !TryWalkToEdge(element, rootVisual, inside.X, inside.Y, 0, -1, WalkStep, out double top)
            || !TryWalkToEdge(element, rootVisual, inside.X, inside.Y, 0, 1, WalkStep, out double bottom))
        {
            return false;
        }
        if (right - left <= 1 || bottom - top <= 1)
        {
            return false;
        }
        // 边界值全程按 double 算，只在最后构造 Rect 时取整到最近的像素再转成 float，
        // 免得二分过程中因为 float 精度反复抖动（截断对负坐标会偏，所以用 Round）
        float leftEdge = (float)Math.Round(left);
        float topEdge = (float)Math.Round(top);
        rect = new Rect(leftEdge, topEdge,
                        (float)Math.Round(right) - leftEdge,
                        (float)Math.Round(bottom) - topEdge);
        return true;
    }



    /// <summary>
    /// 元素是否已经布局出来（有大小、可见）。
    /// </summary>
    private static bool IsMeasurable(FrameworkElement element)
    {
        return element.ActualWidth > 0
            && element.ActualHeight > 0
            && element.Visibility is Visibility.Visible;
    }



    /// <summary>
    /// 在窗口范围里按 <paramref name="step"/> 铺点，返回第一个命中 <paramref name="element"/> 的点。
    /// 从左上角往右下角扫，所以返回的点一般落在元素的左上角附近。
    /// </summary>
    private static Point? FindPointInsideElement(FrameworkElement element, FrameworkElement rootVisual, double step)
    {
        double maxX = Math.Max(rootVisual.ActualWidth, 512) + step;
        double maxY = Math.Max(rootVisual.ActualHeight, 512) + step;
        for (double y = step; y <= maxY; y += step)
        {
            for (double x = step; x <= maxX; x += step)
            {
                // Windows.Foundation.Point 的两个参数同样是 float
                Point point = new((float)x, (float)y);
                if (IsPointOnElement(point, element, rootVisual))
                {
                    return point;
                }
            }
        }
        return null;
    }



    /// <summary>
    /// 从 (<paramref name="insideX"/>, <paramref name="insideY"/>) 沿
    /// (<paramref name="dx"/>, <paramref name="dy"/>) 方向往外走，返回最后一个命中该元素的坐标：
    /// 先按 <paramref name="step"/> 大步走到边界外，再在最后一段里二分逼近到 1px 以内。
    /// 走到窗口外都没出去就算失败。坐标一律按 double 传，避免 float 精度把步进吃掉。
    /// </summary>
    private static bool TryWalkToEdge(FrameworkElement element, FrameworkElement rootVisual, double insideX, double insideY, int dx, int dy, double step, out double edge)
    {
        // (insideX, insideY) 是这个元素范围内的一点，它一定命中
        bool horizontal = dx != 0;
        double origin = horizontal ? insideX : insideY;
        edge = origin;
        double lastInside = 0;
        double maxDistance = Math.Max(rootVisual.ActualWidth, rootVisual.ActualHeight) + 1024;
        for (double distance = step; distance <= maxDistance; distance += step)
        {
            Point point = new((float)(insideX + dx * distance), (float)(insideY + dy * distance));
            if (!IsPointOnElement(point, element, rootVisual))
            {
                double low = lastInside;
                double high = distance;
                while (high - low > 1)
                {
                    double middle = (low + high) / 2;
                    Point middlePoint = new((float)(insideX + dx * middle), (float)(insideY + dy * middle));
                    if (IsPointOnElement(middlePoint, element, rootVisual))
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle;
                    }
                }
                edge = origin + (horizontal ? dx : dy) * low;
                return true;
            }
            lastInside = distance;
        }
        return false;
    }



    /// <summary>
    /// 窗口坐标 <paramref name="windowPoint"/> 命中测试的第一个元素是不是
    /// <paramref name="element"/> 自己或它的子元素（用它自己的边界判定，
    /// 所以「指针是不是在弹出层的内边距环上」这类判断也落在同一个元素范围里）。
    /// </summary>
    private static bool IsPointOnElement(Point windowPoint, FrameworkElement element, FrameworkElement rootVisual)
    {
        try
        {
            foreach (UIElement hit in VisualTreeHelper.FindElementsInHostCoordinates(windowPoint, rootVisual))
            {
                return ReferenceEquals(hit, element) || IsDescendantOf(hit, element);
            }
        }
        catch { }
        return false;
    }



    /// <summary>
    /// 指针坐标是不是落在这张大图的矩形里（本控件坐标系）。
    /// </summary>
    private static bool IsPointerOnPoster(Grid? tile, Point position)
    {
        if (tile is null)
        {
            return false;
        }
        return position.X >= 0
            && position.Y >= 0
            && position.X <= tile.ActualWidth
            && position.Y <= tile.ActualHeight;
    }



    /// <summary>
    /// 把 <paramref name="element"/> 左上角换算到本控件的坐标系。
    /// 只用于还在同一棵树里的元素（大图、窗口根）：跨弹出层边界的那种换算不可靠。
    /// </summary>
    private Point? TransformToGameSelector(UIElement element)
    {
        return TransformToVisualRoot(element, this);
    }



    /// <summary>
    /// 把 <paramref name="element"/> 左上角换算到 <paramref name="root"/> 的坐标系。
    /// 两个元素不在同一个视觉树里时平台可能给出恒等变换，那样会得到 (0,0)，
    /// 于是这里对「换算根本没有发生」的情况返回 null，交给调用方换一种算法或直接放弃。
    /// </summary>
    private static Point? TransformToVisualRoot(UIElement element, UIElement root)
    {
        try
        {
            GeneralTransform transform = element.TransformToVisual(root);
            if (transform is null)
            {
                return null;
            }
            Point point = transform.TransformPoint(new Point(0, 0));
            // 元素自己的 RenderTransform（例如区服列表项的平移）不一定算在里面，补上
            if (element.RenderTransform is Transform renderTransform)
            {
                point = renderTransform.TransformPoint(point);
            }
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                return null;
            }
            if (point.X == 0 && point.Y == 0 && !ReferenceEquals(element, root))
            {
                // 恒等变换：元素到 root 之间隔着弹出层边界
                return null;
            }
            return point;
        }
        catch
        {
            return null;
        }
    }



    /// <summary>
    /// 本控件的 XamlRoot。控件自己还没接上时从视觉树往上找。
    /// </summary>
    private XamlRoot? GetGameSelectorXamlRoot()
    {
        if (this.XamlRoot is XamlRoot root)
        {
            return root;
        }
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is FrameworkElement element && element.XamlRoot is XamlRoot elementRoot)
            {
                return elementRoot;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }



    /// <summary>
    /// 浮窗尺寸变化（预设数量变化、列表内容变高变矮）后重新贴到锚点下方 / 左侧 / 右侧。
    /// </summary>
    private void LaunchProfileFlyout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionLaunchProfileFlyout();
    }



    /// <summary>
    /// 当前显示着预设浮窗的区服项（用于给图标加高亮）。为 null 时不显示。
    /// </summary>
    public GameBizIcon? LaunchProfileIconSource { get; set => SetProperty(ref field, value); }



    private void RefreshLaunchProfileHighlight()
    {
        GameBizIcon? source = _launchProfileAnchor is null
            ? null
            : GameBizIcons.FirstOrDefault(x => x.GameBiz == _launchProfileGameBiz);
        if (!ReferenceEquals(LaunchProfileIconSource, source))
        {
            if (LaunchProfileIconSource is not null)
            {
                LaunchProfileIconSource.IsLaunchProfileActive = false;
            }
            LaunchProfileIconSource = source;
            if (source is not null)
            {
                source.IsLaunchProfileActive = true;
            }
        }
    }



    /// <summary>
    /// 鼠标移入预设浮窗：不要收起。同时清掉缓存的指针坐标，
    /// 免得计时器到点时拿「停在大图上」的旧坐标把列表留下来。
    /// 遮罩也要留：指针从大图挪到浮窗上，大图那次 PointerExited 不应该把「选择服务器」关掉。
    /// </summary>
    private void LaunchProfileFlyout_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        CancelHideServerList(onLaunchProfile: true);
        _cachedPointerPosition = null;
        if (_serverListAnchor is Grid tile)
        {
            UpdatePosterOverlay(tile);
        }
    }



    /// <summary>
    /// 鼠标移出预设浮窗：延迟收起，可以继续移回大图或区服列表。
    /// 遮罩在这里不关：收起计时器到点后由 <see cref="HideServerListIfPointerLeft"/>
    /// 统一决定，那一步会连带把遮罩一起收掉（<see cref="RestorePosterOverlay"/>）。
    /// </summary>
    private void LaunchProfileFlyout_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOnLaunchProfile = false;
        _cachedPointerPosition = null;
        StartHideServerListTimer();
    }


    private void LaunchProfileFlyout_DragStarted(object? sender, EventArgs e)
    {
        _launchProfileDragging = true;
        _hideServerListTimer.Stop();
    }



    private void LaunchProfileFlyout_DragCompleted(object? sender, EventArgs e)
    {
        _launchProfileDragging = false;
        if (!_pointerOnLaunchProfile)
        {
            StartHideServerListTimer();
        }
    }



    /// <summary>
    /// 鼠标移入区服列表：不要收起，并把「停在列表上」记下来。
    /// 遮罩也要留：指针从大图挪到列表上，大图那次 PointerExited 不应该把「选择服务器」关掉。
    /// </summary>
    private void ServerList_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        UpdateCachedPointerPosition(e);
        CancelHideServerList(onServerList: true);
        if (_serverListAnchor is Grid tile)
        {
            UpdatePosterOverlay(tile);
        }
    }



    /// <summary>
    /// 鼠标移出区服列表：延迟收起，可以继续移到大图或预设浮窗上。
    /// 遮罩在这里不关：指针可能又回到大图上（那边有 PointerEntered 会重新点亮）；
    /// 真的都离开了，由计时器到点后的 <see cref="CloseServerListAndLaunchProfile"/> 一起收掉。
    /// </summary>
    private void ServerList_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _cachedPointerPosition = null;
        StartHideServerListTimer();
    }



    private void LaunchProfileFlyout_ProfileSelected(object? sender, GameLaunchProfile profile)
    {
        if (_launchProfileGameBiz is GameBiz gameBiz
            && GameBizIcons.FirstOrDefault(x => x.GameBiz == gameBiz) is GameBizIcon server)
        {
            SelectGameBizAndLaunchProfile(server, profile.Id);
        }
        else
        {
            HideLaunchProfileFlyout();
        }
    }



    /// <summary>
    /// 新增预设：当前启动器页正好是这个区服时新预设继承当前配置，
    /// 见 <see cref="GameLaunchProfileStore.AddProfile"/>。加完直接切到新预设。
    /// </summary>
    private void LaunchProfileFlyout_ProfileAdded(object? sender, string profileId)
    {
        if (_launchProfileGameBiz is GameBiz gameBiz
            && GameBizIcons.FirstOrDefault(x => x.GameBiz == gameBiz) is GameBizIcon server)
        {
            SelectGameBizAndLaunchProfile(server, profileId);
        }
        else
        {
            HideLaunchProfileFlyout();
        }
    }


    #endregion




    #region Full Background 黑色半透明背景


    public bool FullBackgroundVisible => Border_FullBackground.Opacity > 0;


    [RelayCommand]
    private void ShowFullBackground()
    {
        Border_FullBackground.Opacity = 1;
        Border_FullBackground.IsHitTestVisible = true;
        Border_FullBackground.Visibility = Visibility.Visible;
        Border_Pin.Opacity = 1;
        Border_Pin.IsHitTestVisible = true;
        Border_Pin.Visibility = Visibility.Visible;
        GameIconsAreaVisible = true;
    }


    private void HideFullBackground()
    {
        Border_FullBackground.Opacity = 0;
        Border_FullBackground.IsHitTestVisible = false;
        Border_FullBackground.Visibility = Visibility.Collapsed;
        Border_Pin.Opacity = 0;
        Border_Pin.IsHitTestVisible = false;
        Border_Pin.Visibility = Visibility.Collapsed;
        if (!IsPinned)
        {
            GameIconsAreaVisible = false;
        }
    }


    private void Border_FullBackground_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        HideLaunchProfileFlyout();
        CloseServerListAndLaunchProfile();
        var position = e.GetPosition(sender as UIElement);
        if (position.X <= Border_CurrentGameIcon.ActualWidth && position.Y <= Border_CurrentGameIcon.ActualHeight)
        {
            Border_FullBackground.Opacity = 0;
            Border_FullBackground.IsHitTestVisible = false;
            Border_FullBackground.Visibility = Visibility.Collapsed;
            Border_Pin.Opacity = 0;
            Border_Pin.IsHitTestVisible = false;
            Border_Pin.Visibility = Visibility.Collapsed;
        }
        else
        {
            HideFullBackground();
        }
    }


    #endregion





    #region Game Server



    public List<GameBizDisplay> GameBizDisplays { get; set => SetProperty(ref field, value); }




    /// <summary>
    /// 初始化游戏服务器选择区域
    /// </summary>
    private void InitializeGameServerArea(List<GameInfo> gameInfos)
    {
        try
        {
            var list = new List<GameBizDisplay>();

            if (LanguageUtil.FilterLanguage(CultureInfo.CurrentUICulture.Name) is "zh-cn")
            {
                // 当前语言为简体中文时，游戏信息显示从中国官服获取的内容
                foreach (var info in gameInfos)
                {
                    if (info.GameBiz.IsChinaServer() && !info.IsBilibiliServer())
                    {
                        list.Add(new GameBizDisplay { GameInfo = info });
                    }
                }
            }
            else
            {
                // 当前语言不为简体中文时，游戏信息显示从国际服获取的内容
                foreach (var info in gameInfos)
                {
                    if (info.GameBiz.IsGlobalServer())
                    {
                        list.Add(new GameBizDisplay { GameInfo = info });
                    }
                }
            }

            // 分类每个游戏的服务器信息
            foreach (var item in list)
            {
                string game = item.GameInfo.GameBiz.Game;
                foreach (string suffix in (string[])["_cn", "_global", "_bilibili"])
                {
                    GameBiz biz = game + suffix;
                    if (biz.IsKnown())
                    {
                        var server = new GameBizIcon(biz)
                        {
                            IsPinned = GameBizIcons.Any(x => x.GameBiz == biz),
                        };
                        item.Servers.Add(server);
                    }
                    else if (gameInfos.FirstOrDefault(x => x.GameBiz == biz) is GameInfo info)
                    {
                        var server = new GameBizIcon(info)
                        {
                            IsPinned = GameBizIcons.Any(x => x.GameBiz == biz),
                        };
                        item.Servers.Add(server);
                    }
                }
            }
            // 大图列表整个换掉（语言变化 / 游戏信息刷新）时，旧的大图会被丢掉，
            // 挂在它上面的区服列表和预设浮窗可能留在页面上，先收干净
            CloseServerListAndLaunchProfile();
            HideLaunchProfileFlyout();
            ClearPosterOverlayCache();
            GameBizDisplays = new(list);
        }
        catch { }
    }



    /// <summary>
    /// 更新所有游戏服务器信息，更新游戏图标
    /// </summary>
    /// <returns></returns>
    private async Task UpdateGameInfoAsync()
    {
        try
        {
            List<GameInfo> gameInfos = await _hoyoplayService.UpdateGameInfoListAsync();
            InitializeGameServerArea(gameInfos);
            foreach (GameBizIcon icon in GameBizIcons)
            {
                if (icon.GameBiz.IsKnown())
                {
                    icon.UpdateInfo();
                }
                else
                {
                    GameInfo info = await _hoyoplayService.GetGameInfoAsync(icon.GameId);
                    icon.UpdateInfo(info);
                }
            }
            await InitializeInstalledGamesCommand.ExecuteAsync(null);
        }
        catch { }
    }




    /// <summary>
    /// 鼠标移入游戏服务器选择区域：直接展开区服列表，不需要先左键。
    /// 进到大图时先把收起计时器停掉：从上一张大图扫过来时，上一张大图先启动的那次计时器
    /// 会在 <see cref="ShowServerListPanel"/> 里连同上一张大图的列表一起收掉。
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameBizDisplay_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Grid tile)
        {
            _hoveredPosterTile = tile;
            UpdateCachedPointerPosition(e);
            CancelHideServerList();
            UpdatePosterOverlay(tile);
            ShowServerListPanel(tile);
        }
    }



    /// <summary>
    /// 鼠标移出游戏服务器选择区域：先不关，给用户挪到区服列表 / 预设浮窗上的时间。
    /// 进和出的先后顺序不固定，所以分两种情况：
    /// <list type="bullet">
    /// <item>鼠标已经移到另一张大图上（<c>PointerEntered</c> 先到，这张大图已经不是
    /// <see cref="_hoveredPosterTile"/>）：直接把这边的列表和预设浮窗收掉，
    /// 不启动计时器，否则到点会把刚展开的那张大图的列表也收掉；</item>
    /// <item>鼠标真的移开了：只启动计时器，到点时用缓存的指针坐标确认它没有停在大图上
    /// （平台把指针交给新打开的弹出层时，大图会收到一次没有鼠标移出、
    /// 坐标却还停在大图里的 <c>PointerExited</c>，那种不能收）。</item>
    /// </list>
    /// <para>
    /// 遮罩不在这里关，这是这一版的关键：大图和区服列表之间还有 <see cref="ServerListBelowPosterGap"/>
    /// 那一段空隙，指针经过它的时候只会收到大图的 <c>PointerExited</c>、还收不到列表的
    /// <c>PointerEntered</c>。要是在这里就把遮罩收掉，指针进列表再点亮，看起来就是在闪。
    /// 所以这里只启动已有的收起计时器；计时器到点前指针进到列表 / 预设浮窗上会取消它，
    /// 遮罩全程不被碰。真的都离开了，由 <see cref="CloseServerListAndLaunchProfile"/> 连遮罩一起收掉。
    /// </para>
    /// </summary>
    private void Grid_GameBizDisplay_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Grid tile && !ReferenceEquals(_hoveredPosterTile, tile))
        {
            // 指针已经到另一张大图上了：这边的列表连带遮罩一起收掉，那边会自己点亮自己的遮罩
            CloseServerListAndLaunchProfile();
            return;
        }
        _cachedPointerPosition = null;
        if (!_launchProfileDragging)
        {
            StartHideServerListTimer();
        }
    }



    /// <summary>
    /// 显示某张大图的「选择服务器」遮罩：Logo 淡出、Icon &amp; Name 淡入。
    /// 数值和以前那两套 Storyboard 完全一致（Logo → 0、IconAndName → 1），
    /// 平滑由 XAML 里那两个 <c>OpacityTransition</c> 承担，所以观感不变。
    /// </summary>
    private void UpdatePosterOverlay(Grid tile)
    {
        try
        {
            ApplyPosterOverlay(tile, showOverlay: true);
        }
        catch { }
    }



    /// <summary>
    /// 收起某张大图的「选择服务器」遮罩：Logo 淡回、Icon &amp; Name 淡出。
    /// </summary>
    private void RestorePosterOverlay(Grid tile)
    {
        try
        {
            ApplyPosterOverlay(tile, showOverlay: false);
        }
        catch { }
    }



    private void ApplyPosterOverlay(Grid tile, bool showOverlay)
    {
        if (!_posterOverlayCache.TryGetValue(tile, out PosterOverlay? overlay))
        {
            overlay = new PosterOverlay
            {
                Logo = FindDescendantByName<FrameworkElement>(tile, "Grid_GameBizDisplay_Logo"),
                IconAndName = FindDescendantByName<FrameworkElement>(tile, "Grid_GameBizDisplay_IconAndName"),
            };
            _posterOverlayCache[tile] = overlay;
        }
        if (overlay.Logo is null || overlay.IconAndName is null)
        {
            _posterOverlayCache.Remove(tile);
            return;
        }
        overlay.Logo.Opacity = showOverlay ? 0 : 1;
        overlay.IconAndName.Opacity = showOverlay ? 1 : 0;
    }



    /// <summary>
    /// 大图列表整个换掉时清掉遮罩元素缓存，换掉的大图不会再回来。
    /// </summary>
    private void ClearPosterOverlayCache()
    {
        _posterOverlayCache.Clear();
    }



    /// <summary>
    /// 点击服务器图标：切到该区服并使用它的第一个启动预设。
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Button_GameServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon server)
            {
                SelectGameBiz(server);
                HideLaunchProfileFlyout();
                CloseServerListAndLaunchProfile();
                HideFullBackground();
                CurrentGameChanged?.Invoke(this, (server.GameId, false));
            }
        }
        catch { }
    }



    /// <summary>
    /// 鼠标移入区服项：额外弹出该区服的启动预设浮窗。
    /// 浮窗贴着这个区服项本身（在该行右侧），而不是贴在它所在的大图旁边。
    /// 指针还停在列表上，这一次不移出列表，所以顺手把收起计时器停掉。
    /// </summary>
    private void Button_GameServer_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        UpdateCachedPointerPosition(e);
        CancelHideServerList();
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon server)
        {
            // 区服项在弹出层里，跨弹出层边界的换算不可靠，
            // 位置由 PositionLaunchProfileFlyout 用「列表矩形 + 行在列表里的相对位置」算出来
            ShowLaunchProfileFlyout(server.GameBiz, fe);
        }
    }



    /// <summary>
    /// 鼠标移出区服项：延迟收起预设浮窗，可以继续移动到浮窗上。
    /// </summary>
    private void Button_GameServer_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _cachedPointerPosition = null;
        StartHideServerListTimer();
    }




    #region 区服列表面板


    /// <summary>
    /// 悬停大图时展开该游戏的区服列表（共用一块窗口树里的面板 <c>Border_Servers</c>）。
    /// 面板只是画和定位，开 / 收全部由本类决定：同一张大图上重复触发只停计时器，
    /// 不重复设 <c>Visibility</c>（重复开合看起来就是在闪）。
    /// 换到另一张大图时先把上一张大图的列表和预设浮窗收起来，否则旧列表会留在页面上。
    /// </summary>
    private void ShowServerListPanel(Grid tile)
    {
        try
        {
            if (ReferenceEquals(_serverListAnchor, tile) && Border_Servers.Visibility is Visibility.Visible)
            {
                // 已经开着这张大图的列表了：只停掉收起计时器，不产生任何开合
                CancelHideServerList();
                return;
            }
            if (!ReferenceEquals(_serverListAnchor, tile))
            {
                // 换到另一张大图（大图 A → 大图 B 的扫过）：上一张的列表和预设浮窗先收掉
                CloseServerListAndLaunchProfile();
                _serverListAnchor = tile;
            }
            // 列表内容来自这张大图自己的那份区服集合（大图的 DataContext 就是 GameBizDisplay）。
            // 面板只有一份、共用，所以每次显示时把它指向当前这张大图的集合
            if (tile.DataContext is GameBizDisplay display)
            {
                ItemsControl_Servers.ItemsSource = display.Servers;
            }
            Border_Servers.Visibility = Visibility.Visible;
            _serverListItems = ItemsControl_Servers;
            PositionServerListPanel();
        }
        catch { }
    }



    /// <summary>
    /// 在 <paramref name="root"/> 的子树里按名字找 <typeparamref name="T"/>。
    /// </summary>
    private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }
            if (FindDescendantByName<T>(child, name) is T nested)
            {
                return nested;
            }
        }
        return null;
    }



    /// <summary>
    /// 指针坐标是不是落在打开着的区服列表上（本控件坐标系）。
    /// 面板现在和本控件在同一棵视觉树里，所以直接用「列表外框在本控件坐标系里的矩形」判定，
    /// 不再需要跨弹出层边界的命中测试（内边距环仍然算在内，因为量的是外框）。
    /// </summary>
    private bool IsPointerOnServerList(Point position)
    {
        if (Border_Servers.Visibility is not Visibility.Visible
            || TransformToGameSelector(Border_Servers) is not Point listPoint)
        {
            return false;
        }
        return position.X >= listPoint.X
            && position.Y >= listPoint.Y
            && position.X <= listPoint.X + Border_Servers.ActualWidth
            && position.Y <= listPoint.Y + Border_Servers.ActualHeight;
    }



    /// <summary>
    /// <paramref name="element"/> 是否位于 <paramref name="ancestor"/> 的子树里。
    /// 沿着视觉树向上找。
    /// </summary>
    private static bool IsDescendantOf(DependencyObject? element, DependencyObject? ancestor)
    {
        if (element is null || ancestor is null)
        {
            return false;
        }
        DependencyObject? node = element;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }


    #endregion


    /// <summary>
    /// 固定游戏服务器图标到待选区
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Button_PinGameBiz_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon server)
        {
            var biz = server.GameBiz;
            if (GameBizIcons.FirstOrDefault(x => x.GameBiz == biz) is GameBizIcon icon)
            {
                GameBizIcons.Remove(icon);
                server.IsPinned = false;
            }
            else
            {
                GameBizIcons.Add(server);
                server.IsPinned = true;
            }
        }
    }




    #endregion





    #region Installed Games


    /// <summary>
    /// 已安装游戏的实际占用空间
    /// </summary>
    public string? InstalledGamesActualSize { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 已安装游戏的总空间
    /// </summary>
    public string? InstalledGamesSavedSize { get; set => SetProperty(ref field, value); }




    public ObservableCollection<GameBizIcon> InstalledGames { get; set; } = new();



    private CancellationTokenSource? _initializeInstalledGamesCancellationTokenSource;


    /// <summary>
    /// 初始化已安装游戏列表，计算已安装游戏的实际占用空间
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task InitializeInstalledGamesAsync()
    {
        const double GB = 1 << 30;
        try
        {
            _initializeInstalledGamesCancellationTokenSource?.Cancel();
            _initializeInstalledGamesCancellationTokenSource = new();
            CancellationToken token = _initializeInstalledGamesCancellationTokenSource.Token;

            InstalledGames.Clear();
            InstalledGamesActualSize = null;
            InstalledGamesSavedSize = null;
            List<FileInfo> files = new();

            foreach (GameBizDisplay display in GameBizDisplays)
            {
                List<FileInfo> _duplicateFiles = new();
                int serverCount = 0;
                foreach (GameBizIcon server in display.Servers)
                {
                    string? installPath = GameLauncherService.GetGameInstallPath(server.GameId);
                    if (Directory.Exists(installPath))
                    {
                        server.InstallPath = installPath;
                        var _files = new DirectoryInfo(installPath).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                        server.TotalSize = _files.Sum(x => x.Length);
                        InstalledGames.Add(server);
                        if (_files.Count() > 0)
                        {
                            serverCount++;
                            _duplicateFiles.AddRange(_files);
                        }
                    }
                }
                if (serverCount > 1)
                {
                    files.AddRange(_duplicateFiles);
                }
            }
            long totalSize = InstalledGames.Sum(x => x.TotalSize);
            InstalledGamesActualSize = $"{totalSize / GB:F2}GB";

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (files.Count > 0)
            {
                (long fileSize, long actualSize) = await Task.Run(() =>
                {
                    long size = 0;
                    Dictionary<string, long> dic = new();
                    foreach (var file in files)
                    {
                        size += file.Length;
                        using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var idInfo = Kernel32.GetFileInformationByHandleEx<Kernel32.FILE_ID_INFO>(handle, Kernel32.FILE_INFO_BY_HANDLE_CLASS.FileIdInfo);
                        var idInfoBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref idInfo, 1));
                        dic[Convert.ToHexString(idInfoBytes)] = file.Length;
                    }
                    return (size, dic.Values.Sum());
                }, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                InstalledGamesActualSize = $"{(totalSize - fileSize + actualSize) / GB:F2}GB";
                InstalledGamesSavedSize = $"{(fileSize - actualSize) / GB:F2}GB";
            }
            WeakReferenceMessenger.Default.Send(new InstalledGameRefreshedMessage());
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }




    /// <summary>
    /// 从注册表自动搜索已安装的游戏
    /// </summary>
    [RelayCommand]
    public void AutoSearchInstalledGames()
    {
        try
        {
            List<GameInfo> gameInfos = GameLauncherService.GetCachedGameInfos();
            var sb = new StringBuilder();
            foreach (GameInfo item in gameInfos)
            {
                GameBiz gameBiz = item.GameBiz;
                if (item.IsBilibiliServer())
                {
                    gameBiz = $"{gameBiz.Game}_bilibili";
                }
                string? path = GameLauncherService.GetGameInstallPath(gameBiz);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (Directory.Exists(path) || AppConfig.GetGameInstallPathRemovable(gameBiz))
                    {
                        sb.Append(gameBiz);
                        sb.Append(',');
                        continue;
                    }
                }
                string key = "";
                if (gameBiz.Server is "cn")
                {
                    key = $@"HKEY_CURRENT_USER\Software\miHoYo\HYP\1_1\{gameBiz}";
                }
                else if (gameBiz.Server is "global")
                {
                    key = $@"HKEY_CURRENT_USER\Software\Cognosphere\HYP\1_0\{gameBiz}";
                }
                if (!string.IsNullOrWhiteSpace(key))
                {
                    path = Registry.GetValue(key, "GameInstallPath", null) as string;
                    if (Directory.Exists(path))
                    {
                        AppConfig.SetGameInstallPath(gameBiz, path);
                        sb.Append(gameBiz);
                        sb.Append(',');
                        continue;
                    }
                }
            }
            AppConfig.SelectedGameBizs = sb.ToString().TrimEnd(',');
            InitializeGameSelector();
            if (!IsPinned)
            {
                Pin();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }




    /// <summary>
    /// 防止点击已安装游戏列表时，触发 <see cref="Border_FullBackground_Tapped(object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs)"/>
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Expander_InstalledGamesActualSize_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }



    #endregion







}


