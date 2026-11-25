using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Starward.Features.GameRecord.DailyNote.Notify;
using Starward.Features.PlayTime;
using Starward.Features.GameRecord.SignIn;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.GameSelector;
using Starward.Features.Setting;
using Starward.Features.Startup;
using Starward.Helpers;
using Starward.GameInstall;
using Starward.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics;


namespace Starward.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class SystemTrayWindow : WindowBase
{


    /// <summary>
    /// 实时便笺提醒：同样由托盘窗口驱动常驻检查，实例来自 <c>AppConfig.ServiceProvider.cs</c>。
    /// </summary>
    private readonly DailyNoteNotifyService _dailyNoteNotifyService = AppConfig.GetService<DailyNoteNotifyService>();


    private readonly AutoSignInService _autoSignInService = AppConfig.GetService<AutoSignInService>();
    private readonly ILogger<SystemTrayWindow> _logger = AppConfig.GetLogger<SystemTrayWindow>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();

    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();


    /// <summary>收起预设列表的延时计时器，见 <see cref="HideLaunchProfileIfPointerLeft"/>。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideLaunchProfileTimer;


    /// <summary>装载预设列表的独立悬浮窗口。第一次悬停到有多个预设的区服行时才创建。</summary>
    private GameLaunchProfilePopupWindow? _launchProfilePopup;


    /// <summary>当前悬停的那一行区服，预设列表显示的就是它的预设。</summary>
    private PinnedGameIcon? _hoveredServer;


    /// <summary>指针是不是正停在预设列表上。</summary>
    private bool _pointerOnLaunchProfile;


    /// <summary>指针是不是正停在托盘菜单上（列表收起后决定菜单要不要一起收）。</summary>
    private bool _pointerOnMenu;


    /// <summary>托盘菜单当前是不是活动窗口。</summary>
    private bool _isActive;



    public SystemTrayWindow()
    {
        this.InitializeComponent();
        // 收起预设面板的延时计时器：和完整游戏启动器里收起预设浮窗同一个时长。
        _hideLaunchProfileTimer = DispatcherQueue.CreateTimer();
        _hideLaunchProfileTimer.Interval = TimeSpan.FromMilliseconds(350);
        _hideLaunchProfileTimer.IsRepeating = false;
        _hideLaunchProfileTimer.Tick += (_, _) => HideLaunchProfileIfPointerLeft();
        InitializeWindow();
        SetTrayIcon();
        _ = InitializeInstalledGamesAsync();
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => this.Bindings.Update());
        // 常驻实例的对外通道：开着它，点击系统通知的协议进程才能找到本实例并转交「打开主窗口」请求。
        ResidentInstanceMessenger.StartListening();
        // 实时便笺提醒：常驻检查各账号便笺，达到阈值时发 Windows 系统通知。
        _dailyNoteNotifyService.StartResident();
        // 安装更新时安装器会结束安装目录下的 Starward 进程（含记录时长的子进程）；游戏还在运行时接续记录。
        // 托盘窗口在每种启动方式下都会创建且只创建一次，放在这里保证每次启动只检查一次。
        _ = Task.Run(() => AppConfig.GetService<PlayTimeRecordService>().ResumeInterruptedRecordings());
        // 托盘窗口是常驻状态下唯一必然存在且不会销毁的窗口，每日自动签到挂在这里：
        // 启动后先签一轮，之后跨日再签。
        _autoSignInService.StartResident();
        WeakReferenceMessenger.Default.Register<InstalledGameRefreshedMessage>(this, (_, _) => UpdateInstalledGames());
        WeakReferenceMessenger.Default.Register<GameInstallPathChangedMessage>(this, (_, _) => _ = InitializeInstalledGamesAsync());
    }




    private unsafe void InitializeWindow()
    {
        new SystemBackdropHelper(this, SystemBackdropProperty.AcrylicDefault with
        {
            TintColorLight = 0xFFE7E7E7,
            TintColorDark = 0xFF404040
        }).TrySetAcrylic(true);

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (s, e) => e.Cancel = true;
        this.Activated += SystemTrayWindow_Activated;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var flag = User32.GetWindowLongPtr(WindowHandle, User32.WindowLongFlags.GWL_STYLE);
        flag &= ~(nint)User32.WindowStyles.WS_CAPTION;
        flag &= ~(nint)User32.WindowStyles.WS_BORDER;
        User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_STYLE, flag);
        var p = DwmApi.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        DwmApi.DwmSetWindowAttribute(WindowHandle, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, (nint)(&p), sizeof(DwmApi.DWM_WINDOW_CORNER_PREFERENCE));
        // 去掉窗口边框：不做这一步，DWM 会给这个窗口描一圈深色边（托盘菜单看起来就是围了一圈黑边）。
        uint noBorder = 0xFFFFFFFE;
        DwmApi.DwmSetWindowAttribute(WindowHandle, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, (nint)(&noBorder), sizeof(uint));

        Show();
        Hide();
    }



    private void SetTrayIcon()
    {
        try
        {
            nint hInstance = Kernel32.GetModuleHandle(null).DangerousGetHandle();
            nint hIcon = User32.LoadIcon(hInstance, "#32512").DangerousGetHandle();
            trayIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
        }
        catch { }
    }



    private void SystemTrayWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isActive = args.WindowActivationState is not WindowActivationState.Deactivated;
        if (!_isActive)
        {
            // 预设列表是独立窗口，显示时它会把这个窗口的焦点抢走（亚克力需要活动窗口才是模糊的观感），
            // 这时菜单必须留着，等指针离开或用户点到别处再收，见 HideLaunchProfileIfPointerLeft。
            if (_launchProfilePopup is { } popup && popup.AppWindow.IsVisible)
            {
                return;
            }
            Hide();
        }
    }



    [RelayCommand]
    public override void Show()
    {
        RootGrid.RequestedTheme = ShouldSystemUseDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
        // 每次弹出都从「只有菜单」的状态开始：把上次留下的预设列表先收掉，并刷新已安装游戏。
        HideLaunchProfile();
        UpdateInstalledGames();
        RootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SIZE windowSize = new()
        {
            Width = (int)(RootGrid.DesiredSize.Width * UIScale),
            Height = (int)(RootGrid.DesiredSize.Height * UIScale)
        };
        User32.GetCursorPos(out POINT point);
        User32.CalculatePopupWindowPosition(point, windowSize, User32.TrackPopupMenuFlags.TPM_RIGHTALIGN | User32.TrackPopupMenuFlags.TPM_BOTTOMALIGN | User32.TrackPopupMenuFlags.TPM_WORKAREA, null, out RECT windowPos);
        User32.MoveWindow(WindowHandle, windowPos.X, windowPos.Y, windowPos.Width, windowPos.Height, true);
        base.Show();
    }



    [RelayCommand]
    public override void Hide()
    {
        HideLaunchProfile();
        _pointerOnMenu = false;
        base.Hide();
    }



    [RelayCommand]
    public void ShowMainWindow()
    {
        App.Current.EnsureMainWindow();
    }


    [RelayCommand]
    private void Exit()
    {
        App.Current.Exit();
    }


    protected override nint WindowSubclassProc(HWND hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == (uint)User32.WindowMessage.WM_POWERBROADCAST)
        {
            // 广播给所有顶层窗口，无需 RegisterPowerSettingNotification。不吞消息。
            // PBT_APMRESUMESUSPEND = 7 / PBT_APMRESUMEAUTOMATIC = 0x12 / PBT_APMRESUMECRITICAL = 3
            if (wParam is 7 or 0x12 or 3)
            {
                // 睡眠期间计时器不走，恢复后让常驻循环按墙上时钟重算到期时刻（便笺提醒等）
                _dailyNoteNotifyService.NotifySystemResumed();
            }
        }
        return base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }


    private void WindowEx_Closed(object sender, WindowEventArgs args)
    {
        // 常驻宿主走了（软件退出）：先摘掉对外通道，之后不再接收任何通知激活。
        ResidentInstanceMessenger.StopListening();
        // 预设列表是独立窗口，不跟着宿主一起走，这里显式关掉。
        _launchProfilePopup?.Close();
        _launchProfilePopup = null;
        trayIcon?.Dispose();
    }



    #region 已安装游戏


    /// <summary>
    /// 托盘菜单里列出的已安装游戏。
    /// </summary>
    public ObservableCollection<SystemTrayGameIcon> InstalledGames { get; set; } = new();



    /// <summary>
    /// 先确保每个启动器的游戏信息缓存可用，再刷新菜单行。
    /// </summary>
    private async Task InitializeInstalledGamesAsync()
    {
        try
        {
            var launchers = LauncherConfigModel.GetEnabled();
            try
            {
                await _hoYoPlayService.UpdateLauncherClientMapAsync(launchers.Select(x => x.ToLauncherConfig()));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Update launcher client map for system tray");
            }
        }
        finally
        {
            UpdateInstalledGames();
        }
    }



    /// <summary>
    /// 按本机已记录的安装路径列出可直接启动的游戏。行对象换成新的集合，
    /// 但正在启动 / 运行中的那一行保持变暗。
    /// </summary>
    private void UpdateInstalledGames()
    {
        try
        {
            // 列表要重建，行对象都换人了：先收起预设面板，别让它挂在一个已经不存在的行上。
            HideLaunchProfile();
            var running = InstalledGames.Where(x => x.MaskOpacity < 1).Select(x => x.GameId).ToHashSet();
            InstalledGames.Clear();
            foreach (GameEntry entry in GameEntryService.GetInstalledEntries())
            {
                if (string.IsNullOrWhiteSpace(entry.InstallPath) || !Directory.Exists(entry.InstallPath))
                {
                    continue;
                }
                GameId gameId = new(entry.GameId, entry.GameBiz);
                LauncherConfigModel? launcher = LauncherConfigModel.FromId(entry.LauncherId);
                if (launcher is null)
                {
                    continue;
                }
                GameInfo? gameInfo = _hoYoPlayService.GetCachedGameInfo(gameId);
                if (gameInfo is null)
                {
                    continue;
                }
                var icon = new SystemTrayGameIcon(launcher, gameInfo);
                if (running.Contains(icon.GameId))
                {
                    icon.MaskOpacity = 0.5;
                }
                InstalledGames.Add(icon);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update installed games in system tray");
        }
    }



    private async void Button_StartGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is PinnedGameIcon icon)
            {
                // 直接点区服项就是用该区服的第一个预设启动。
                GameLaunchProfileStore.SetCurrentProfile(icon.GameId.GameBiz, GameLaunchProfileStore.GetFirstProfile(icon.GameId.GameBiz).Id);
                await StartGameFromTrayAsync(icon);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start game from system tray");
        }
    }



    /// <summary>
    /// 从托盘启动某个区服并盯着它退出。当前预设已经由调用方选好（第一条预设，或预设列表里点的那一条），
    /// 这里按存储里的当前预设启动，见 <see cref="GameLauncherService.StartGameAsync"/>。
    /// </summary>
    private async Task StartGameFromTrayAsync(PinnedGameIcon icon)
    {
        Hide();
        Process? process = await _gameLauncherService.StartGameAsync(icon.GameId);
        if (process is not null)
        {
            icon.MaskOpacity = 0.5;
            WeakReferenceMessenger.Default.Send(new GameStartedMessage());
            _ = WatchGameExitAsync(icon, process);
        }
    }



    /// <summary>
    /// 等游戏退出后恢复这一行的显示状态。用 <see cref="Process.Exited"/> 而不是定时轮询，
    /// 窗口隐藏时也不会有多余开销。
    /// </summary>
    private async Task WatchGameExitAsync(PinnedGameIcon icon, Process process)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromHours(24));
            await process.WaitForExitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wait for game exit");
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() => icon.MaskOpacity = 1);
        }
    }


    #endregion



    #region 启动预设列表


    /// <summary>
    /// 悬停区服项：该区服有多个预设时，在菜单左边弹出它的预设列表（独立窗口）。
    /// <para>
    /// 这是托盘里预设列表唯一的显示入口，而且只有一个预设的区服不显示（没有可选项）。
    /// 列表里只有「选预设」：窗口里把 <see cref="GameLaunchProfileFlyout.ShowEditButton"/> 关掉了，
    /// 所以不会有预设编辑窗口从托盘里弹出来（那个窗口会抢焦点、把托盘菜单顶掉）。
    /// </para>
    /// </summary>
    private void Button_StartGame_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement row && row.DataContext is PinnedGameIcon server)
            {
                ShowLaunchProfile(server, row);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show launch profile in system tray");
        }
    }


    /// <summary>鼠标移出区服项：延时收起，留出挪到预设列表上的时间。</summary>
    private void Button_StartGame_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        StartHideLaunchProfileTimer();
    }



    private void ShowLaunchProfile(PinnedGameIcon server, FrameworkElement row)
    {
        _hideLaunchProfileTimer.Stop();
        // 只有一个预设：没有可选的，不显示列表。
        if (GameLaunchProfileStore.GetProfiles(server.GameId.GameBiz).Count <= 1)
        {
            HideLaunchProfile();
            return;
        }
        GameLaunchProfilePopupWindow popup = EnsureLaunchProfilePopup();
        if (ReferenceEquals(_hoveredServer, server) && popup.AppWindow.IsVisible)
        {
            // 同一行上重复触发（例如从行里的图标挪到文字）：不重新开合，免得看起来在闪。
            return;
        }
        _hoveredServer = server;
        popup.ShowAt(server.GameId.GameBiz, AppConfig.GetCurrentLaunchProfileId(server.GameId.GameBiz), GetRowRectOnScreen(row), GetMenuRectOnScreen());
    }



    /// <summary>
    /// 这一行在屏幕上的矩形（物理像素）：托盘窗口的位置 + 行在窗口内容里的位置。
    /// </summary>
    private RectInt32 GetRowRectOnScreen(FrameworkElement row)
    {
        double scale = UIScale;
        Point topLeft = row.TransformToVisual(null).TransformPoint(new Point(0, 0));
        PointInt32 origin = AppWindow.Position;
        return new RectInt32(
            origin.X + (int)Math.Round(topLeft.X * scale),
            origin.Y + (int)Math.Round(topLeft.Y * scale),
            (int)Math.Round(row.ActualWidth * scale),
            (int)Math.Round(row.ActualHeight * scale));
    }



    /// <summary>
    /// 托盘菜单窗口在屏幕上的矩形（物理像素）。预设列表的宽度取它的宽度（和菜单一样宽）、
    /// 位置摆在它左边。
    /// </summary>
    private RectInt32 GetMenuRectOnScreen()
    {
        PointInt32 origin = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        return new RectInt32(origin.X, origin.Y, size.Width, size.Height);
    }



    /// <summary>
    /// 第一次要显示预设列表时创建那个独立窗口，之后一直复用（托盘窗口本身是常驻的）。
    /// </summary>
    private GameLaunchProfilePopupWindow EnsureLaunchProfilePopup()
    {
        if (_launchProfilePopup is null)
        {
            GameLaunchProfilePopupWindow popup = new();
            popup.PanelPointerEntered += (_, _) =>
            {
                _hideLaunchProfileTimer.Stop();
                _pointerOnLaunchProfile = true;
            };
            popup.PanelPointerExited += (_, _) =>
            {
                _pointerOnLaunchProfile = false;
                StartHideLaunchProfileTimer();
            };
            popup.ProfileSelected += LaunchProfilePopup_ProfileSelected;
            // 用户点到别的地方去了（预设列表失去活动状态）：列表和菜单一起收掉。
            popup.Dismissed += (_, _) => Hide();
            _launchProfilePopup = popup;
        }
        return _launchProfilePopup;
    }



    /// <summary>
    /// 预设列表里点了某一条：切到它并立刻用这个预设启动游戏（和点区服项只差一个预设）。
    /// </summary>
    private async void LaunchProfilePopup_ProfileSelected(object? sender, GameLaunchProfile profile)
    {
        try
        {
            if (_hoveredServer is PinnedGameIcon server)
            {
                GameLaunchProfileStore.SetCurrentProfile(server.GameId.GameBiz, profile.Id);
                await StartGameFromTrayAsync(server);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start game from launch profile in system tray");
        }
    }



    private void StartHideLaunchProfileTimer()
    {
        _hideLaunchProfileTimer.Stop();
        _hideLaunchProfileTimer.Start();
    }



    private void HideLaunchProfileIfPointerLeft()
    {
        if (_pointerOnLaunchProfile)
        {
            return;
        }
        HideLaunchProfile();
        // 列表收掉之后：菜单的活动状态是被列表抢走的（_isActive 为 false），如果指针也不在菜单上，
        // 就把菜单一起收掉，免得在屏幕上留一个谁也点不动的菜单。
        if (!_pointerOnMenu && !_isActive)
        {
            Hide();
        }
    }



    /// <summary>指针移进 / 移出托盘菜单本体，用来判断列表收起后菜单该不该跟着收。</summary>
    private void StackPanel_Menu_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOnMenu = true;
    }



    private void StackPanel_Menu_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOnMenu = false;
    }



    /// <summary>
    /// 收起预设列表。已经收起时什么都不做（不重复开关窗口）。
    /// </summary>
    private void HideLaunchProfile()
    {
        _hideLaunchProfileTimer.Stop();
        _pointerOnLaunchProfile = false;
        _hoveredServer = null;
        if (_launchProfilePopup is { } popup && popup.AppWindow.IsVisible)
        {
            popup.Hide();
        }
    }


    #endregion


}
