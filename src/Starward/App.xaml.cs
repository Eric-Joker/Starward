using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Starward.Core;
using Starward.Features.Database;
using Starward.Features.GameSelector;
using Starward.Features.GamepadControl;
using Starward.Features.Passport;
using Starward.Features.Setting;
using Starward.Features.ViewHost;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Timers;
using Vanara.PInvoke;
using Windows.ApplicationModel.Activation;


namespace Starward;

public partial class App : Application
{

    private readonly DispatcherQueue _uiDispatcherQueue;

    private readonly Timer _gcTimer = new(TimeSpan.FromSeconds(60));

    public static new App Current => (App)Application.Current;


    public App()
    {
        this.InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
        _uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += App_UnhandledException;
        _gcTimer.Elapsed += (_, _) => GC.Collect();
        _gcTimer.Start();
    }


    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        string logFile = AppConfig.LogFile;
        if (string.IsNullOrWhiteSpace(logFile))
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starward", "log");
            Directory.CreateDirectory(logFolder);
            logFile = Path.Combine(logFolder, $"Starward_{DateTime.Now:yyMMdd}.log");
        }
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App Crash:");
        sb.AppendLine(e.Exception.ToString());
        if (e.Exception.Data.Count > 0)
        {
            foreach (DictionaryEntry item in e.Exception.Data)
            {
                sb.AppendLine($"{item.Key}: {item.Value}");
            }
        }
        using var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var sw = new StreamWriter(fs);
        sw.Write(sb);
    }


    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs _)
    {
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();


        if (AppConfig.UserDataFolder is null)
        {
            if (await new WelcomeWindow().WaitAsync())
            {
                AppConfig.LoadConfiguration();
            }
            else
            {
                Environment.Exit(0);
            }
        }

        instance = AppInstance.FindOrRegisterForKey("Starward.Main");
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(instance.GetActivatedEventArgs());
            Environment.Exit(0);
        }

        // 手柄引导键自愈：上次进程被强杀或崩溃可能把 GameBar 引导键停在「已接管」状态，
        // 当前配置并非「总是接管」时先还原，避免注册表残留导致引导键永久按不出 GameBar。
        // 只在本实例（即将成为常驻主实例）执行，rpc/playtime 等子进程已在上面提前返回。
        GamepadController.RestoreGamepadGuideButtonIfNotAlwaysTakenOver();

        instance = AppInstance.GetCurrent();
        instance.Activated += AppInstance_Activated;

        // 程序挪过目录 / 应用更新过之后，把仍启用的开机自启快捷方式指回当前 exe；用户已关掉自启则不动。
        AutoStartService.RepairIfNeeded();
        if (Environment.GetCommandLineArgs().Contains("--hide"))
        {
            m_SystemTrayWindow = new SystemTrayWindow();
        }
        else
        {
            m_MainWindow = new MainWindow();
            m_MainWindow.Activate();
        }
    }




    private AppInstance instance;

    private MainWindow m_MainWindow;

    private SystemTrayWindow m_SystemTrayWindow;



    /// <summary>
    /// 点击系统通知后要切到的游戏；本分支的游戏切换器在主窗口里创建，所以先记在这里，等它初始化时取走。
    /// 只在主窗口还没建好时用（见 <see cref="EnsureMainWindow(GameBiz?)"/>）。
    /// </summary>
    private static GameBiz? _pendingSelectGameBiz;


    /// <summary>
    /// 打开主窗口并切到指定游戏（点击系统通知时）。主窗口还没建时先记下目标游戏，建好后游戏切换器按它切换；
    /// 已建好时发 <see cref="SelectGameBizMessage"/> 让游戏切换器切换。
    /// </summary>
    /// <param name="biz">要切到的游戏区服；为空或无法识别时只打开主窗口。</param>
    public void EnsureMainWindow(GameBiz? biz)
    {
        if (biz is { } target && target.IsKnown())
        {
            if (m_MainWindow is null)
            {
                _pendingSelectGameBiz = target;
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new SelectGameBizMessage(target));
            }
        }
        EnsureMainWindow();
    }


    /// <summary>
    /// 主窗口还没建好时，把点击系统通知要切到的游戏记下来。由通知激活进程在 XAML 启动前设置。
    /// </summary>
    /// <param name="biz">要切到的游戏区服。</param>
    public static void SetPendingSelectGameBiz(GameBiz biz)
    {
        _pendingSelectGameBiz = biz;
    }


    /// <summary>
    /// 查看待切换的游戏，不移除；游戏切换器初始化与图标异步补全时用它重试。
    /// </summary>
    /// <returns>待切换的游戏区服；没有时返回 <see langword="null"/>。</returns>
    public static GameBiz? PeekPendingSelectGameBiz()
    {
        return _pendingSelectGameBiz;
    }


    /// <summary>
    /// 取走待切换的游戏。游戏切换器每轮只切一次，避免主窗口重建时又切一遍。
    /// </summary>
    /// <returns>待切换的游戏区服；没有时返回 <see langword="null"/>。</returns>
    public static GameBiz? ConsumePendingSelectGameBiz()
    {
        GameBiz? biz = _pendingSelectGameBiz;
        _pendingSelectGameBiz = null;
        return biz;
    }


    public void EnsureMainWindow()
    {
        m_MainWindow ??= new MainWindow();
        m_MainWindow.Activate();
        m_MainWindow.Show();
    }


    public void EnsureSystemTray()
    {
        m_SystemTrayWindow ??= new SystemTrayWindow();
    }



    private void AppInstance_Activated(object? sender, AppActivationArguments e)
    {
        try
        {
            _uiDispatcherQueue.TryEnqueue(EnsureMainWindow);
            if (e.Data is ILaunchActivatedEventArgs { Arguments: string arguments })
            {
                string?[] args = Shell32.CommandLineToArgvW(arguments);
                if (args.Length > 1)
                {
                    string? url = args[1];
                    if (url?.StartsWith("starward-hyp-login://") ?? false)
                    {
                        WeakReferenceMessenger.Default.Send(new HypThirdPartyLoginMessage(url));
                    }
                }
            }
        }
        catch { }
    }



    public static AppInstance? FindInstanceForKey(string key)
    {
        foreach (var item in AppInstance.GetInstances())
        {
            if (item.Key == key)
            {
                return item;
            }
        }
        return null;
    }



    public async void Exit(bool quickExit = false)
    {
        // 仅当这个被置为 0 的引导键确实是我们接管时才还原，否则会把用户自己在 GameBar 里关掉的引导键又打开。
        GamepadController.RestoreGamepadGuideButtonOnExit();
        m_MainWindow?.Close();
        m_SystemTrayWindow?.Close();
        GamepadController.RestoreGamepadGuideButtonForGameBar();
        if (!quickExit)
        {
            try
            {
                var timer = new Timer(30_000);
                timer.Elapsed += (_, _) => Environment.Exit(0);
                timer.Start();
                AppInstance.GetCurrent().UnregisterKey();
                DatabaseService.AutoBackupToAppDataLocal();
            }
            catch { }
        }
        Application.Current.Exit();
    }



}
