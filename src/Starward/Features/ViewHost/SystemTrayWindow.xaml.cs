using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.CloudGame;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord.AutoRefresh;
using Starward.Features.GameRecord.SignIn;
using Starward.Features.GameSelector;
using Starward.Features.PlayTime;
using Starward.Features.Setting;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics;


namespace Starward.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class SystemTrayWindow : WindowEx
{


    private readonly ILogger<SystemTrayWindow> _logger = AppConfig.GetLogger<SystemTrayWindow>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();

    private readonly AutoCloudGameFreeTimeService _autoCloudGameFreeTimeService = AppConfig.GetService<AutoCloudGameFreeTimeService>();

    private readonly AutoSignInService _autoSignInService = AppConfig.GetService<AutoSignInService>();

    /// <summary>
    /// 自动更新战绩：与上面两个常驻循环挂在同一个窗口上，
    /// 实例来自 <c>AppConfig.ServiceProvider.cs</c> 注册的 <c>AddSingleton&lt;AutoRecordRefreshService&gt;()</c>。
    /// </summary>
    private readonly AutoRecordRefreshService _autoRecordRefreshService = AppConfig.GetService<AutoRecordRefreshService>();


    /// <summary>收起预设列表的延时计时器，见 <see cref="HideLaunchProfileIfPointerLeft"/>。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideLaunchProfileTimer;


    /// <summary>装载预设列表的独立悬浮窗口。第一次悬停到有多个预设的区服行时才创建。</summary>
    private GameLaunchProfilePopupWindow? _launchProfilePopup;


    /// <summary>当前悬停的那一行区服，预设列表显示的就是它的预设。</summary>
    private GameBizIcon? _hoveredServer;


    /// <summary>指针是不是正停在预设列表上。</summary>
    private bool _pointerOnLaunchProfile;


    /// <summary>指针是不是正停在托盘菜单上（列表收起后决定菜单要不要一起收）。</summary>
    private bool _pointerOnMenu;


    /// <summary>托盘菜单当前是不是活动窗口。</summary>
    private bool _isActive;




    public SystemTrayWindow()
    {
        this.InitializeComponent();
        // 收起预设面板的延时计时器：和完整游戏选择器里区服列表收起预设浮窗同一个时长。
        _hideLaunchProfileTimer = DispatcherQueue.CreateTimer();
        _hideLaunchProfileTimer.Interval = TimeSpan.FromMilliseconds(350);
        _hideLaunchProfileTimer.IsRepeating = false;
        _hideLaunchProfileTimer.Tick += (_, _) => HideLaunchProfileIfPointerLeft();
        UpdateInstalledGames();
        InitializeWindow();
        SetTrayIcon();
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => this.Bindings.Update());
        WeakReferenceMessenger.Default.Register<InstalledGameRefreshedMessage>(this, (_, _) => UpdateInstalledGames());
        WeakReferenceMessenger.Default.Register<GameInstallPathChangedMessage>(this, (_, _) => UpdateInstalledGames());
        // 托盘窗口是常驻状态下唯一必然存在且不会销毁的窗口，云游戏免费时长的领取循环挂在这里。
        _autoCloudGameFreeTimeService.StartResident();
        // 每日自动签到同样挂在常驻窗口上：启动后签一轮，之后跨日再签。
        _autoSignInService.StartResident();
        // 战绩自动更新：等云游戏免费时长那轮启动批量打完（它自己又排在签到首轮之后）再检查一次到期板块。
        _autoRecordRefreshService.StartResident(_autoCloudGameFreeTimeService.StartupBatchCompleted);
        // 安装更新时安装器会结束安装目录下的 Starward 进程（含记录时长的子进程）；游戏还在运行时接续记录。
        _ = Task.Run(() => AppConfig.GetService<PlayTimeRecordService>().ResumeInterruptedRecordings());
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
        // 每次弹出都从「只有菜单」的状态开始：把上次留下的预设列表先收掉。
        HideLaunchProfile();
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


    private void WindowEx_Closed(object sender, WindowEventArgs args)
    {
        // 常驻宿主走了（软件退出）：取消还没跑完的自动更新战绩，之后不再发起任何请求。
        _autoRecordRefreshService.StopResident();
        // 预设列表是独立窗口，不跟着宿主一起走，这里显式关掉。
        _launchProfilePopup?.Close();
        _launchProfilePopup = null;
        trayIcon?.Dispose();
    }




    /// <summary>
    /// 托盘菜单里列出的已安装游戏。
    /// </summary>
    public ObservableCollection<GameBizIcon> InstalledGames { get; set; } = new();




    private void UpdateInstalledGames()
    {
        try
        {
            // 列表要重建，行对象都换人了：先收起预设面板，别让它挂在一个已经不存在的行上。
            HideLaunchProfile();
            InstalledGames.Clear();
            foreach (GameBizDisplay display in GetGameServerArea(GameLauncherService.GetCachedGameInfos()))
            {
                foreach (GameBizIcon server in display.Servers)
                {
                    // GameId.FromGameBiz 对少数区服没有映射（例如 bh3_bilibili 落到默认的 null），
                    // 所以不能假设每个已知区服都有 GameId，否则托盘每次刷新都会抛 NullReferenceException。
                    if (server.GameId is null)
                    {
                        continue;
                    }
                    string? path = GameLauncherService.GetGameInstallPath(server.GameId, out bool storageRemoved);
                    if (!storageRemoved && Directory.Exists(path))
                    {
                        server.TextOpacity = 1;
                        InstalledGames.Add(server);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update installed games in system tray");
        }
    }




    /// <summary>
    /// 按当前语言挑选游戏信息的来源（简体中文从中国官服取，其余从国际服取），
    /// 再展开每个游戏的全部服务器。
    /// </summary>
    private static List<GameBizDisplay> GetGameServerArea(List<GameInfo> gameInfos)
    {
        var list = new List<GameBizDisplay>();
        if (LanguageUtil.FilterLanguage(CultureInfo.CurrentUICulture.Name) is "zh-cn")
        {
            // 当前语言为简体中文时，游戏信息显示从中国官服获取的内容
            foreach (GameInfo info in gameInfos)
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
            foreach (GameInfo info in gameInfos)
            {
                if (info.GameBiz.IsGlobalServer())
                {
                    list.Add(new GameBizDisplay { GameInfo = info });
                }
            }
        }
        // 分类每个游戏的服务器信息
        foreach (GameBizDisplay item in list)
        {
            string game = item.GameInfo.GameBiz.Game;
            foreach (string suffix in (string[])["_cn", "_global", "_bilibili"])
            {
                GameBiz biz = game + suffix;
                if (biz.IsKnown() || gameInfos.FirstOrDefault(x => x.GameBiz == biz) is GameInfo info)
                {
                    item.Servers.Add(new GameBizIcon(biz));
                }
            }
        }
        return list;
    }




    private async void Button_StartGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is GameBizIcon icon && icon.GameId is GameId gameId)
            {
                // 直接点区服项就是用该区服的第一个预设启动（和完整选择器里点区服项选第一个预设同义）。
                GameLaunchProfileStore.SetCurrentProfile(icon.GameBiz, GameLaunchProfileStore.GetFirstProfile(icon.GameBiz).Id);
                await StartGameFromTrayAsync(icon, gameId);
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
    private async Task StartGameFromTrayAsync(GameBizIcon icon, GameId gameId)
    {
        Hide();
        Process? process = await _gameLauncherService.StartGameAsync(gameId);
        if (process is not null)
        {
            icon.TextOpacity = 0.5;
            WeakReferenceMessenger.Default.Send(new GameStartedMessage());
            _ = WatchGameExitAsync(icon, process);
        }
    }



    #region 启动预设列表


    /// <summary>
    /// 悬停区服项：该区服有多个预设时，在菜单左边弹出它的预设列表（独立窗口）。
    /// <para>
    /// 这是托盘里预设列表<b>唯一</b>的显示入口——没有右键、长按之类的其他触发方式，
    /// 而且只有一个预设的区服不显示（没有可选项，显示出来只是多一层遮挡）。
    /// </para>
    /// <para>
    /// 列表里只有「选预设」：窗口里把 <see cref="GameLaunchProfileFlyout.ShowEditButton"/> 关掉了，
    /// 所以不会有预设编辑窗口从托盘里弹出来（那个窗口会抢焦点、把托盘菜单顶掉）。
    /// 以后若要让托盘也能进编辑窗口，得把「编辑期间不收起菜单」那套保护加回来。
    /// </para>
    /// </summary>
    private void Button_StartGame_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement row && row.DataContext is GameBizIcon server)
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



    private void ShowLaunchProfile(GameBizIcon server, FrameworkElement row)
    {
        _hideLaunchProfileTimer.Stop();
        // 只有一个预设：没有可选的，不显示列表（托盘里唯一的显示逻辑）。
        if (GameLaunchProfileStore.GetProfiles(server.GameBiz).Count <= 1)
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
        popup.ShowAt(server.GameBiz, AppConfig.GetCurrentLaunchProfileId(server.GameBiz), GetRowRectOnScreen(row), GetMenuRectOnScreen());
    }



    /// <summary>
    /// 这一行在屏幕上的矩形（物理像素）：托盘窗口的位置 + 行在窗口内容里的位置。
    /// 预设列表按它做行锚定（列表里第一行的顶边对齐这一行的顶边），见
    /// <see cref="GameLaunchProfilePopupWindow.ShowAt"/>。
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
            if (_hoveredServer is GameBizIcon server && server.GameId is GameId gameId)
            {
                GameLaunchProfileStore.SetCurrentProfile(server.GameBiz, profile.Id);
                await StartGameFromTrayAsync(server, gameId);
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




    /// <summary>
    /// 等游戏退出后恢复这一行的显示状态。用 <see cref="Process.Exited"/> 而不是定时轮询，
    /// 窗口隐藏时也不会有多余开销。
    /// </summary>
    private async Task WatchGameExitAsync(GameBizIcon icon, Process process)
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
            DispatcherQueue.TryEnqueue(() => icon.TextOpacity = 1);
        }
    }


}
