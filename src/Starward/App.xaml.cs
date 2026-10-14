using Microsoft.Extensions.Configuration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.GamepadControl;
using Starward.Features.Setting;
using Starward.Features.UrlProtocol;
using Starward.Features.ViewHost;
using Starward.RPC;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;


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

        if (args.Length > 0 && args[0].ToLower().StartsWith("starward://test/"))
        {
            new TestUrlProtocolWindow().Activate();
            return;
        }

        await AppConfig.CheckEnviromentAsync();

        // 程序挪过目录之后，把仍启用的开机自启快捷方式指回当前 exe；用户已关掉自启则不动。
        AutoStartService.RepairIfNeeded();

        if (args.Length > 0 && await HandleStartupAsync(args))
        {
            return;
        }

        // 手柄引导键自愈：上次进程被强杀或崩溃可能把 GameBar 引导键停在「已接管」状态，
        // 当前配置并非「总是接管」时先还原，避免注册表残留导致引导键永久按不出 GameBar。
        // 只在本实例（即将成为常驻主实例）执行，rpc/playtime 等子进程已在上面提前返回。
        GamepadController.RestoreGamepadGuideButtonIfNotAlwaysTakenOver();

        instance = AppInstance.GetCurrent();
        instance.Activated += AppInstance_Activated;

        var main = AppInstance.FindOrRegisterForKey("main");
        if (!main.IsCurrent)
        {
            await main.RedirectActivationToAsync(instance.GetActivatedEventArgs());
            Environment.Exit(0);
        }
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


    private async Task<bool> HandleStartupAsync(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder().AddCommandLine(args).Build();
        if (args[0].ToLower() is "rpc")
        {
            RpcRunner.Run(args);
            Environment.Exit(0);
        }
        if (args[0].ToLower() is "playtime")
        {
            int pid = config.GetValue<int>("pid");
            GameBiz biz = (GameBiz)config.GetValue<string>("biz");
            if (pid > 0)
            {
                var playtime = AppConfig.GetService<Features.PlayTime.PlayTimeRecordService>();
                await playtime.LogPlayTimeAsync(biz, pid);
            }
            Environment.Exit(0);
        }

        if (args[0].ToLower() is "startgame")
        {
            GameBiz biz = (GameBiz)config.GetValue<string>("biz");
            GameId? gameId = GameId.FromGameBiz(biz);
            if (gameId is not null)
            {
                await AppConfig.GetService<GameLauncherService>().StartGameAsync(gameId);
            }
            Environment.Exit(0);
        }

        if (args[0].ToLower().StartsWith("starward://"))
        {
            if (await UrlProtocolService.HandleUrlProtocolAsync(args[0]))
            {
                Environment.Exit(0);
            }
        }

        return false;
    }



    private AppInstance instance;

    private MainWindow m_MainWindow;

    private SystemTrayWindow m_SystemTrayWindow;



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
        _uiDispatcherQueue.TryEnqueue(EnsureMainWindow);
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



    public new void Exit()
    {
        // 仅当这个被置为 0 的引导键确实是我们接管时才还原，否则会把用户自己在 GameBar 里关掉的引导键又打开。
        GamepadController.RestoreGamepadGuideButtonOnExit();
        m_MainWindow?.Close();
        m_SystemTrayWindow?.Close();
        Application.Current.Exit();
    }



}
