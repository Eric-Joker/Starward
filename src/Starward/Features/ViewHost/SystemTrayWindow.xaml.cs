using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Starward.Features.GameRecord.DailyNote.Notify;
using Starward.Features.PlayTime;
using Starward.Features.GameRecord.SignIn;
using Starward.Features.Setting;
using Starward.Features.Startup;
using Starward.Helpers;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation;


namespace Starward.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class SystemTrayWindow : WindowBase
{


    /// <summary>
    /// 实时便笺提醒：同样由托盘窗口驱动常驻检查，实例来自 <c>AppConfig.ServiceProvider.cs</c>。
    /// </summary>
    private readonly DailyNoteNotifyService _dailyNoteNotifyService = AppConfig.GetService<DailyNoteNotifyService>();


    private readonly AutoSignInService _autoSignInService = AppConfig.GetService<AutoSignInService>();


    public SystemTrayWindow()
    {
        this.InitializeComponent();
        InitializeWindow();
        SetTrayIcon();
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
        if (args.WindowActivationState is WindowActivationState.Deactivated)
        {
            Hide();
        }
    }



    [RelayCommand]
    public override void Show()
    {
        RootGrid.RequestedTheme = ShouldSystemUseDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
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
        trayIcon?.Dispose();
    }


}
