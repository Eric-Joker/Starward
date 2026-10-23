using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Starward.Features.Background;
using Starward.Features.Database;
using Starward.Features.GameLauncher;
using Starward.Features.Overlay;
using Starward.Features.Screenshot;
using Starward.Frameworks;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Graphics;


namespace Starward.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class MainWindow : WindowEx
{


    public static new MainWindow Current { get; private set; }


    public MainWindow()
    {
        Current = this;
        MainWindowId = AppWindow.Id;
        this.InitializeComponent();
        InitializeMainWindow();
        App.Current.EnsureSystemTray();
        WeakReferenceMessenger.Default.Register<AccentColorChangedMessage>(this, OnAccentColorChanged);
        WeakReferenceMessenger.Default.Register<GameStartedMessage>(this, OnGameStarted);
    }



    private void InitializeMainWindow()
    {
        Title = "Starward";
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.ShowIconAndSystemMenu;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Closing += AppWindow_Closing;
        Content.KeyDown += Content_KeyDown;
        CenterInScreen(1200, 676);
        AdaptTitleBarButtonColorToActuallTheme();
        SetDragRectangles(new RectInt32(0, 0, 100000, (int)(48 * UIScale)));
        SetIcon();
        WTSRegisterSessionNotification(WindowHandle, 0);
        // 显示器开关：熄屏后帧服务器模式的 MediaPlayer 等不到垂直同步会空转吃满一个核心，背景视频据此释放 / 重建播放器
        RegisterPowerSettingNotification(WindowHandle, in GUID_SESSION_DISPLAY_STATUS, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
    }



    public override void Show()
    {
        double uiScale = UIScale;
        if (Math.Abs(AppWindow.Size.Width - 1200 * uiScale) > 10 || Math.Abs(AppWindow.Size.Height - 676 * uiScale) > 10)
        {
            CenterInScreen(1200, 676);
        }
        base.Show();
    }



    public void ShowByGamepad()
    {
        CenterInScreen(1200, 676);
        User32.SetCursorPos(AppWindow.Position.X + AppWindow.Size.Width / 2, AppWindow.Position.Y + AppWindow.Size.Height / 2);
        base.Show();
    }



    private void OnGameStarted(object _, GameStartedMessage __)
    {
        StartGameAction action = AppConfig.StartGameAction;
        if (action is StartGameAction.Hide)
        {
            this.Hide();
        }
        else if (action is StartGameAction.Minimize)
        {
            this.Minimize();
        }
    }



    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            args.Cancel = true;
            MainWindowCloseOption option = AppConfig.CloseWindowOption;
            if (option is not MainWindowCloseOption.Hide and not MainWindowCloseOption.Exit)
            {
                var dialog = new MainWindowCloseDialog
                {
                    Title = Lang.ExperienceSettingPage_CloseWindowOption,
                    PrimaryButtonText = Lang.Common_Confirm,
                    CloseButtonText = Lang.Common_Cancel,
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                var result = await dialog.ShowAsync();
                if (result is not ContentDialogResult.Primary)
                {
                    return;
                }
                option = dialog.MainWindowCloseOption.Value;
                AppConfig.CloseWindowOption = option;
            }
            if (option is MainWindowCloseOption.Hide)
            {
                Hide();
            }
            if (option is MainWindowCloseOption.Exit)
            {
                Close();
                AppInstance.GetCurrent().UnregisterKey();
                Task backupTask = Task.Run(DatabaseService.AutoBackupToAppDataLocal);
                Task timeTask = Task.Delay(30000);
                await Task.WhenAny(backupTask, timeTask);
                App.Current.Exit();
            }
        }
        catch { }
    }



    private void OnAccentColorChanged(object _, AccentColorChangedMessage __)
    {
        FrameworkElement ele = (FrameworkElement)Content;
        ele.RequestedTheme = ele.ActualTheme switch
        {
            ElementTheme.Light => ElementTheme.Dark,
            ElementTheme.Dark => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        ele.RequestedTheme = ElementTheme.Default;
    }



    private void Content_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Escape)
        {
            Hide();
        }
    }



    public override void Hide()
    {
        base.Hide();
        WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { Hide = true, CurrentTime = DateTimeOffset.Now });
        GC.Collect();
    }



    private DateTimeOffset _lastActivatedTime = DateTimeOffset.Now;


    protected override nint WindowSubclassProc(HWND hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == (uint)User32.WindowMessage.WM_ACTIVATE || uMsg == (uint)User32.WindowMessage.WM_POINTERACTIVATE)
        {
            // 窗口激活
            if (wParam is 0x1 or 0x2)
            {
                // WA_ACTIVE or WA_CLICKACTIVE
                var now = DateTimeOffset.Now;
                WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage
                {
                    Activate = true,
                    CurrentTime = now,
                    LastActivatedTime = _lastActivatedTime,
                });
                _lastActivatedTime = now;
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_SYSCOMMAND)
        {
            if (wParam == 0xF030)
            {
                // SC_MAXIMIZE
                // 防止双击标题栏使窗口最大化，WinAppSDK 某个版本的 Bug
                return IntPtr.Zero;
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_WTSSESSION_CHANGE)
        {
            if (wParam == 0x7)
            {
                // WTS_SESSION_LOCK
                // 锁屏，暂停视频背景
                IsSessionLocked = true;
                WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { SessionLock = true, CurrentTime = DateTimeOffset.Now });
            }
            else if (wParam == 0x8)
            {
                // WTS_SESSION_UNLOCK
                IsSessionLocked = false;
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_POWERBROADCAST && wParam == 0x8013 /* PBT_POWERSETTINGCHANGE */)
        {
            OnPowerSettingChanged(lParam);
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_DEVICECHANGE)
        {
            // 存储设备插入/拔出
            if (wParam == 0x8000)
            {
                // DBT_DEVICEARRIVAL
                User32.DEV_BROADCAST_HDR dev = Marshal.PtrToStructure<User32.DEV_BROADCAST_HDR>(lParam);
                if (dev.dbch_devicetype is User32.DBT_DEVTYPE.DBT_DEVTYP_VOLUME)
                {
                    WeakReferenceMessenger.Default.Send(new RemovableStorageDeviceChangedMessage());
                }
            }
            else if (wParam == 0x8004)
            {
                // DBT_DEVICEREMOVECOMPLETE
                User32.DEV_BROADCAST_HDR dev = Marshal.PtrToStructure<User32.DEV_BROADCAST_HDR>(lParam);
                if (dev.dbch_devicetype is User32.DBT_DEVTYPE.DBT_DEVTYP_VOLUME)
                {
                    WeakReferenceMessenger.Default.Send(new RemovableStorageDeviceChangedMessage());
                }
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_HOTKEY)
        {
            if (wParam == 44444)
            {
                if (!RunningGameService.OpenOverlayWindow())
                {
                    this.Show();
                }
            }
            else if (wParam == 44445)
            {
                // 截图
                ScreenCaptureService.Capture();
            }
        }
        return base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }



    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);



    /// <summary>
    /// 当前会话是否处于锁屏状态（由 WTS 锁屏 / 解锁通知维护）。
    /// </summary>
    public bool IsSessionLocked { get; private set; }


    /// <summary>
    /// 当前会话的显示器是否已关闭。调暗不算关闭：调暗时仍有垂直同步。
    /// </summary>
    public bool IsDisplayOff { get; private set; }


    /// <summary>
    /// GUID_SESSION_DISPLAY_STATUS：本会话显示器的开关状态。交互式会话里的应用应订阅它，而不是 GUID_CONSOLE_DISPLAY_STATE。
    /// </summary>
    private static readonly Guid GUID_SESSION_DISPLAY_STATUS = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");


    /// <summary>
    /// 处理 PBT_POWERSETTINGCHANGE：显示器在关闭与开启之间切换时更新 <see cref="IsDisplayOff"/>，
    /// 并广播 <see cref="MainWindowDisplayStatusChangedMessage"/> 的熄屏 / 亮屏消息。
    /// 注册时系统会先发一次当前状态，与已知状态相同则不广播。
    /// </summary>
    /// <param name="lParam">指向 POWERBROADCAST_SETTING 的指针。</param>
    private void OnPowerSettingChanged(nint lParam)
    {
        if (lParam == 0 || Marshal.PtrToStructure<Guid>(lParam) != GUID_SESSION_DISPLAY_STATUS)
        {
            return;
        }
        // POWERBROADCAST_SETTING = Guid(16) + DataLength(4) + Data；Data 为 MONITOR_DISPLAY_STATE：0 关 / 1 开 / 2 调暗
        bool off = Marshal.ReadInt32(lParam, 20) == 0;
        if (off == IsDisplayOff)
        {
            return;
        }
        IsDisplayOff = off;
        WeakReferenceMessenger.Default.Send(new MainWindowDisplayStatusChangedMessage { DisplayOff = off, DisplayOn = !off, CurrentTime = DateTimeOffset.Now });
    }


    /// <summary>
    /// 注册电源设置变化通知，变化时向窗口发送 WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE。
    /// </summary>
    /// <param name="hRecipient">接收通知的窗口句柄。</param>
    /// <param name="powerSettingGuid">要订阅的电源设置 GUID。</param>
    /// <param name="flags">0 表示 <paramref name="hRecipient"/> 是窗口句柄（DEVICE_NOTIFY_WINDOW_HANDLE）。</param>
    /// <returns>通知句柄；失败返回 0。窗口销毁后通知自然停止，进程内不再注销。</returns>
    [LibraryImport("user32.dll")]
    private static partial nint RegisterPowerSettingNotification(nint hRecipient, in Guid powerSettingGuid, int flags);


}
