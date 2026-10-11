using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.GameSelector;
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


namespace Starward.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class SystemTrayWindow : WindowEx
{


    private readonly ILogger<SystemTrayWindow> _logger = AppConfig.GetLogger<SystemTrayWindow>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();




    public SystemTrayWindow()
    {
        this.InitializeComponent();
        UpdateInstalledGames();
        InitializeWindow();
        SetTrayIcon();
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => this.Bindings.Update());
        WeakReferenceMessenger.Default.Register<InstalledGameRefreshedMessage>(this, (_, _) => UpdateInstalledGames());
        WeakReferenceMessenger.Default.Register<GameInstallPathChangedMessage>(this, (_, _) => UpdateInstalledGames());
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


    private void WindowEx_Closed(object sender, WindowEventArgs args)
    {
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
            InstalledGames.Clear();
            foreach (GameBizDisplay display in GetGameServerArea(GameLauncherService.GetCachedGameInfos()))
            {
                foreach (GameBizIcon server in display.Servers)
                {
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
                Process? process = await _gameLauncherService.StartGameAsync(gameId);
                if (process is not null)
                {
                    icon.TextOpacity = 0.5;
                    WeakReferenceMessenger.Default.Send(new GameStartedMessage());
                    _ = WatchGameExitAsync(icon, process);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start game from system tray");
        }
    }




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
