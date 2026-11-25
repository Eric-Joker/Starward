global using Starward.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Windows.AppLifecycle;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.GameSelector;
using Starward.Features.Notification;
using Starward.Features.RPC;
using Starward.Features.Startup;
using Starward.Features.UrlProtocol;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Starward;

#if DISABLE_XAML_GENERATED_MAIN

/// <summary>
/// Program class
/// </summary>
public static class Program
{


    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2609")]
    //[global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.STAThreadAttribute]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        AppConfig.CheckEnviroment();
        HandleStartupAsync(args).GetAwaiter().GetResult();

        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }


    private static async Task HandleStartupAsync(string[] args)
    {
        if (args.Length is 0 || AppConfig.UserDataFolder is null)
        {
            return;
        }

        // 点击系统通知：通知平台以普通权限拉起本进程（参数是 starward-notify://open/?biz=…&uid=…）。
        // 常驻实例可能提权，改走 WM_COPYDATA 让它切游戏并打开主窗口，本进程随即退出；
        // 没有常驻实例（软件已退出、点的是通知中心里的旧通知）时放行，按普通启动继续，并记下要切到的游戏。
        if (NotificationActivation.IsActivationArgument(args[0]))
        {
            _ = NotificationActivation.TryParseOpen(args[0], out GameBiz? notifyBiz, out long notifyUid);
            if (ResidentInstanceMessenger.NotifyOpenMainWindow(notifyBiz, notifyUid))
            {
                Environment.Exit(0);
            }
            // 主窗口建好后游戏切换器按这里记下的目标切换（本分支没有启动参数处理管道，只能先记着）
            if (notifyBiz is { } target && target.IsKnown())
            {
                App.SetPendingSelectGameBiz(SelectGameBizMessage.ResolveTarget(GetCurrentGameBiz(), target));
            }
            return;
        }

        string firstArg = args[0].ToLower();
        IConfiguration config = new ConfigurationBuilder().AddCommandLine(args).Build();
        if (firstArg is "rpc")
        {
            RpcRunner.Run(args);
            Environment.Exit(0);
        }
        if (firstArg is "playtime")
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

        if (firstArg is "startgame")
        {
            GameBiz biz = (GameBiz)config.GetValue<string>("biz");
            GameId? gameId = GameId.FromGameBiz(biz);
            if (gameId is not null)
            {
                await AppConfig.GetService<GameLauncherService>().StartGameAsync(gameId);
            }
            Environment.Exit(0);
        }

        if (firstArg.StartsWith("starward://"))
        {
            if (await UrlProtocolService.HandleUrlProtocolAsync(args[0]))
            {
                Environment.Exit(0);
            }
        }

        if (firstArg.StartsWith("starward-hyp-login://"))
        {
            try
            {
                var main = AppInstance.FindOrRegisterForKey("Starward.Main");
                if (!main.IsCurrent)
                {
                    await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                }
            }
            finally
            {
                Environment.Exit(0);
            }
        }
    }


    /// <summary>
    /// 当前主界面选中的游戏区服。游戏选择设置只存了 GameId，按固定图标表反查区服；
    /// 查不到（首次运行、图标已删）时按未知处理，让通知的目标直接生效。
    /// </summary>
    /// <returns>当前游戏区服；无法确定时为空值。</returns>
    private static GameBiz GetCurrentGameBiz()
    {
        try
        {
            string? currentGameId = AppConfig.CurrentGameId;
            if (!string.IsNullOrWhiteSpace(currentGameId))
            {
                return new GameBiz(GameEntryService.GetPinnedEntries().FirstOrDefault(x => x.GameId == currentGameId)?.GameBiz);
            }
        }
        catch { }
        return default;
    }




    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        string logFile = AppConfig.LogFile;
        if (string.IsNullOrWhiteSpace(logFile))
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starward", "log");
            Directory.CreateDirectory(logFolder);
            logFile = Path.Combine(logFolder, $"Starward_{DateTime.Now:yyMMdd}.log");
        }
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program Crash:");
        sb.AppendLine(e.ExceptionObject.ToString());
        if (e.ExceptionObject is Exception { Data.Count: > 0 } ex)
        {
            foreach (DictionaryEntry item in ex.Data)
            {
                sb.AppendLine($"{item.Key}: {item.Value}");
            }
        }
        using var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var sw = new StreamWriter(fs);
        sw.Write(sb);
    }
}

#endif


