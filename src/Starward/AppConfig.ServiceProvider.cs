using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Starward.Core.CloudGame;
using Starward.Core.Blackboard;
using Starward.Core.Gacha.Genshin;
using Starward.Core.Gacha.StarRail;
using Starward.Core.Gacha.ZZZ;
using Starward.Core.GameNotice;
using Starward.Core.GameRecord;
using Starward.Core.HoYoPlay;
using Starward.Core.MiyoLive;
using Starward.Core.Passport;
using Starward.Core.SelfQuery;
using Starward.Features.Account;
using Starward.Features.Background;
using Starward.Features.CloudGame;
using Starward.Features.Database;
using Starward.Features.Gacha;
using Starward.Features.Gacha.UIGF;
using Starward.Features.GameAccount;
using Starward.Features.GameInstall;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord;
using Starward.Features.GameRecord.AutoRefresh;
using Starward.Features.GameRecord.SignIn;
using Starward.Features.HoYoPlay;
using Starward.Features.PlayTime;
using Starward.Features.RPC;
using Starward.Features.RedeemCode;
using Starward.Features.Screenshot;
using Starward.Features.SelfQuery;
using Starward.Features.TimeNode;
using Starward.Features.Update;
using Starward.Setup.Core;
using System;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Starward;

public static partial class AppConfig
{

    private static IServiceProvider _serviceProvider;


    private static void BuildServiceProvider()
    {
        if (_serviceProvider == null)
        {
            var logFolder = Path.Combine(CacheFolder, "log");
            Directory.CreateDirectory(logFolder);
            LogFile = Path.Combine(logFolder, $"Starward_{DateTime.Now:yyMMdd}.log");
            Log.Logger = new LoggerConfiguration().WriteTo.File(path: LogFile, shared: true, outputTemplate: $$"""[{Timestamp:HH:mm:ss.fff}] [{Level:u4}] [{{Path.GetFileName(Environment.ProcessPath)}} ({{Environment.ProcessId}})] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}""")
                                                  .Enrich.FromLogContext()
                                                  .CreateLogger();
            Log.Information($"Welcome to Starward v{AppVersion}\r\nSystem: {Environment.OSVersion}\r\nCommand Line: {Environment.CommandLine}");

            var sc = new ServiceCollection();
            sc.AddMemoryCache();
            sc.AddLogging(c => c.AddSerilog(Log.Logger));
            sc.AddHttpClient().ConfigureHttpClientDefaults(ConfigDefaultHttpClient);

            sc.AddSingleton<HoYoPlayClient>();
            sc.AddSingleton<GameNoticeClient>();
            sc.AddSingleton<HoYoPlayService>();
            sc.AddSingleton<BackgroundService>();

            // 米游社百科（好感壁纸的数据来源）与好感壁纸服务。
            sc.AddSingleton<BlackboardClient>();
            sc.AddSingleton<FavorWallpaperService>();
            sc.AddSingleton<GameLauncherService>();
            sc.AddSingleton<GamePackageService>();
            sc.AddSingleton<PlayTimeRecordService>();
            sc.AddSingleton<PlayTimeStatsService>();
            sc.AddSingleton<GameNoticeService>();
            sc.AddSingleton<SetupService>();

            sc.AddSingleton<GenshinGachaClient>();
            sc.AddSingleton<StarRailGachaClient>();
            sc.AddSingleton<ZZZGachaClient>();
            sc.AddSingleton<GenshinGachaService>();
            sc.AddSingleton<StarRailGachaService>();
            sc.AddSingleton<ZZZGachaService>();
            sc.AddSingleton<UIGFGachaService>();
            sc.AddSingleton<GenshinBeyondGachaClient>();
            sc.AddSingleton<GenshinBeyondGachaService>();

            sc.AddSingleton<HoyolabClient>();
            sc.AddSingleton<HyperionClient>();
            sc.AddSingleton<GameRecordService>();

            // 每日签到：服务本身无状态，自动签到由托盘窗口在常驻期间驱动。
            sc.AddSingleton<SignInService>();
            sc.AddSingleton<AutoSignInService>();

            // 首页时间节点与前瞻兑换码：由启动器右侧工具栏的两个按钮各自取用。
            sc.AddSingleton<MiyoLiveClient>();
            sc.AddSingleton<TimeNodeService>();
            sc.AddSingleton<RedeemCodeService>();

            // 战绩自动更新：常驻循环由托盘窗口驱动，配置面板与错误弹窗直接取服务。
            sc.AddSingleton<AutoRecordRefreshService>();

            sc.AddSingleton<SelfQueryClient>();
            sc.AddSingleton<SelfQueryService>();

            sc.AddHttpClient<ReleaseClient>().ConfigStarwardHttpClient();
            sc.AddTransient<UpdateService>();

            sc.AddSingleton<RpcService>();
            sc.AddSingleton<GameInstallService>();

            sc.AddSingleton<GameAuthLoginService>();
            sc.AddSingleton<GameAccountService>();

            // 通行证客户端也走统一的 HttpClient 配置：连接池、自动解压、HTTP/2|3 与系统代理。
            // User-Agent 会在发送时被 PassportClient 覆盖成米游社 App 的，其它默认头不影响通行证接口。
            sc.AddHttpClient<PassportClient>().ConfigStarwardHttpClient();
            sc.AddSingleton<PassportAccountService>();

            sc.AddSingleton<ScreenCaptureService>();

            // 云游戏钱包与每日免费时长。凭证只从本机云游戏客户端的日志里读，不经过任何第三方服务。
            sc.AddHttpClient<CloudGameClient>().ConfigStarwardHttpClient();
            sc.AddSingleton<CloudGameClientCredentialProvider>();
            sc.AddSingleton<CloudGameWalletService>();
            sc.AddSingleton<AutoCloudGameFreeTimeService>();

            sc.AddHttpClient<LogUploadClient>().ConfigStarwardHttpClient();


            _serviceProvider = sc.BuildServiceProvider();
        }
    }

    public static T GetService<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<T>()!;
    }

    public static ILogger<T> GetLogger<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<ILogger<T>>()!;
    }

    public static SqliteConnection CreateDatabaseConnection()
    {
        return DatabaseService.CreateConnection();
    }


    private static void ConfigDefaultHttpClient(this IHttpClientBuilder builder)
    {
        builder.RemoveAllLoggers();
        builder.ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Clear();
#if DEBUG
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward.Debug/{AppVersion}");
#else
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward/{AppVersion}");
#endif
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        });
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
    }


    private static void ConfigStarwardHttpClient(this IHttpClientBuilder builder)
    {
        builder.RemoveAllLoggers();
        builder.ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Clear();
#if DEBUG
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward.Debug/{AppVersion}");
#else
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward/{AppVersion}");
#endif
            client.DefaultRequestHeaders.Add("X-Sw-Device-Id", DeviceId.ToString());
            client.DefaultRequestHeaders.Add("X-Sw-Session-Id", SessionId.ToString());
            client.DefaultRequestHeaders.Add("X-Sw-App-Version", AppVersion);
            client.DefaultRequestHeaders.Add("X-Sw-App-Type", "Desktop");
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        });
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
    }


}