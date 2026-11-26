using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Background;
using Starward.Features.CloudGame;
using Starward.Features.GameInstall;
using Starward.Features.Overlay;
using Starward.Features.ViewHost;
using Starward.GameInstall;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;


namespace Starward.Features.GameLauncher;

public sealed partial class GameLauncherPage : PageBase
{

    private readonly ILogger<GameLauncherPage> _logger = AppConfig.GetLogger<GameLauncherPage>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();

    private readonly GameInstallService _gameInstallService = AppConfig.GetService<GameInstallService>();

    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();


    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _dispatchTimer;


    public GameLauncherPage()
    {
        this.InitializeComponent();
        _dispatchTimer = DispatcherQueue.CreateTimer();
        _dispatchTimer.Interval = TimeSpan.FromMilliseconds(100);
        _dispatchTimer.Tick += UpdateGameInstallTaskProgress;
    }



    protected override void OnLoaded()
    {
        InitializeGameFeature();
        CheckGameVersion();
        UpdateGameInstallTask();
        CheckCloudGame();
        _ = InitializeGameServerAsync();
        WeakReferenceMessenger.Default.Register<GameInstallPathChangedMessage>(this, OnGameInstallPathChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<RemovableStorageDeviceChangedMessage>(this, OnRemovableStorageDeviceChanged);
        WeakReferenceMessenger.Default.Register<GameInstallTaskStartedMessage>(this, OnGameInstallTaskStarted);
        WeakReferenceMessenger.Default.Register<GameLaunchProfileChangedMessage>(this, OnGameLaunchProfileChanged);

    }



    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _dispatchTimer.Tick -= UpdateGameInstallTaskProgress;
        _dispatchTimer.Stop();
    }



    private void InitializeGameFeature()
    {
        GameFeatureConfig feature = GameFeatureConfig.FromGameId(CurrentGameId);
        if (feature.SupportCloudGame)
        {
            Button_CloudGame.Visibility = Visibility.Visible;
        }

        InitializeLaunchProfile();
    }




    #region 启动预设


    /// <summary>
    /// 当前区服选中的启动预设。启动参数、DX12、自动登录、自定义背景都跟着它走。
    /// </summary>
    private GameLaunchProfile CurrentLaunchProfile => GameLaunchProfileStore.GetCurrentProfile(CurrentGameBiz);


    /// <summary>
    /// 当前预设的显示名称。显示为「开始游戏」主文案右下角的那行灰色小字，区服只有一个预设时为 null。
    /// </summary>
    public string? CurrentLaunchProfileName { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 只有该区服不止一个预设时才显示当前预设名。
    /// </summary>
    public bool IsLaunchProfileNameVisible { get; set => SetProperty(ref field, value); }


    private void InitializeLaunchProfile()
    {
        try
        {
            var profiles = GameLaunchProfileStore.GetProfiles(CurrentGameBiz);
            IsLaunchProfileNameVisible = profiles.Count > 1;
            CurrentLaunchProfileName = profiles.Count > 1 ? CurrentLaunchProfile.DisplayName : null;
            // 当前预设名是「开始游戏」主文案右下角那行小字的数据源，换预设 / 改名之后
            // 即使新旧值算出一样（同名或同一个预设）也要通知一次，保证换预设必定重新取一遍。
            if (IsLaunchProfileNameVisible)
            {
                OnPropertyChanged(nameof(CurrentLaunchProfileName));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize launch profile {Biz}", CurrentGameBiz);
        }
    }


    /// <summary>
    /// 预设被改名 / 新增 / 删除 / 排序，或者当前预设被换掉之后刷新：
    /// 右下角的预设名、DX12 这类跟着预设走的开关都要重新读一遍。
    /// </summary>
    private void OnGameLaunchProfileChanged(object _, GameLaunchProfileChangedMessage message)
    {
        try
        {
            InitializeLaunchProfile();
            // 当前预设的 DX12 设置可能和页面上显示的不一致了，重新读一遍
            if (DXConfig is not null)
            {
                _enableDX12 = CurrentLaunchProfile.EnableDX12 ?? DXConfig.UseDX12ByDefault;
                OnPropertyChanged(nameof(EnableDX12));
            }
            // 新预设里显式开启过 DX12 时，即使这张显卡不支持切换也要把选项露出来
            IsDX12OptionAvailable = IsDX12OptionAvailable || CurrentLaunchProfile.EnableDX12 is true;
        }
        catch { }
    }






    #endregion



    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledLocateGameEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDX12OptionVisible))]
    public partial GameState GameState { get; set; }



    [RelayCommand]
    private async Task ClickStartGameButtonAsync()
    {
        await Task.Delay(1);
        switch (GameState)
        {
            case GameState.None:
                break;
            case GameState.StartGame:
                await StartGameAsync();
                break;
            case GameState.GameIsRunning:
            case GameState.InstallGame:
                await InstallGameAsync();
                break;
            case GameState.Installing:
                await ChangeGameInstallTaskStateAsync();
                break;
            case GameState.UpdateGame:
                await UpdateGameAsync();
                break;
            case GameState.UpdatePlugin:
                await UpdatePluginAsync();
                break;
            case GameState.ResumeDownload:
                await ResumeDownloadAsync();
                break;
            case GameState.ComingSoon:
                break;
            case GameState.Reservation:
                await OpenReservationUrlAsync();
                break;
            case GameState.HotUpdate:
                await HotUpdateAsync();
                break;
            default:
                break;
        }
    }




    #region Game Server


    public List<GameServerConfig>? GameServers { get; set => SetProperty(ref field, value); }

    [ObservableProperty]
    public partial GameServerConfig? SelectedGameServer { get; set; }
    partial void OnSelectedGameServerChanged(GameServerConfig? oldValue, GameServerConfig? newValue)
    {
        if (oldValue is not null && newValue is not null)
        {
            AppConfig.LastGameIdOfBH3Global = newValue.GameId;
            WeakReferenceMessenger.Default.Send(new BH3GlobalGameServerChangedMessage(newValue.GameId));
        }
    }


    /// <summary>
    /// 初始化区服选项，仅崩坏三国际服使用
    /// </summary>
    /// <returns></returns>
    private async Task InitializeGameServerAsync()
    {
        try
        {
            if (CurrentGameBiz == GameBiz.bh3_global)
            {
                GameInfo? gameInfo = await _hoYoPlayService.GetGameInfoAsync(CurrentGameId);
                if (gameInfo?.GameServerConfigs?.Count > 0)
                {
                    GameServers = gameInfo.GameServerConfigs;
                    if (GameServers.FirstOrDefault(x => x.GameId == CurrentGameId.Id) is GameServerConfig config)
                    {
                        SelectedGameServer = config;
                    }
                    else
                    {
                        SelectedGameServer = GameServers.FirstOrDefault();
                        if (SelectedGameServer is not null)
                        {
                            CurrentGameId = new GameId(SelectedGameServer.GameId, GameBiz.bh3_global);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize game server");
        }
    }



    #endregion



    #region Game Version


    public string? GameInstallPath { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 可移动存储设备提示
    /// </summary>
    public bool IsInstallPathRemovableTipEnabled { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 已安装？定位游戏
    /// </summary>
    public bool InstalledLocateGameEnabled => GameState is GameState.InstallGame && !IsInstallPathRemovableTipEnabled;

    /// <summary>
    /// 预下载按钮是否可用
    /// </summary>
    public bool IsPredownloadButtonEnabled { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 预下载是否完成
    /// </summary>
    public bool IsPredownloadFinished { get; set => SetProperty(ref field, value); }

    private Version? localGameVersion;

    private Version? latestGameVersion;

    private Version? predownloadGameVersion;

    private string? reservationUrl;


    /// <summary>
    /// 当前显卡可切换 DX12，或者用户已经在当前预设里勾选过。启动时先按本地记录显示，不等接口。
    /// </summary>
    private bool IsDX12OptionAvailable { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 是否显示 DX12 选项。只在游戏已安装（开始 / 运行中 / 可更新）时显示，
    /// 未安装时同一位置要显示定位游戏等提示。
    /// </summary>
    public bool IsDX12OptionVisible => IsDX12OptionAvailable && GameState is GameState.StartGame or GameState.GameIsRunning or GameState.UpdateGame;


    /// <summary>
    /// DX12 配置
    /// </summary>
    public GameDXConfig? DXConfig { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 启用 DX12
    /// </summary>
    private bool _enableDX12;
    public bool EnableDX12
    {
        get => _enableDX12; set
        {
            if (SetProperty(ref _enableDX12, value))
            {
                CurrentLaunchProfile.EnableDX12 = value;
                GameLaunchProfileStore.Save(CurrentGameBiz);
            }
        }
    }


    private CancellationTokenSource? _checkGameVersionTokenSource;


    private async void CheckGameVersion()
    {
        try
        {
            _checkGameVersionTokenSource?.Cancel();
            _checkGameVersionTokenSource = CancellationTokenSource.CreateLinkedTokenSource(UnloadCancellationToken);
            CancellationToken cancellation = _checkGameVersionTokenSource.Token;

            GameInstallPath = GameLauncherService.GetGameInstallPath(CurrentGameId, out bool storageRemoved);
            IsInstallPathRemovableTipEnabled = storageRemoved;
            if (GameInstallPath is null || storageRemoved)
            {
                if (await CheckGameReservationAsync(cancellation))
                {
                    Button_PlayTime.Visibility = Visibility.Collapsed;
                    GameState = GameState.Reservation;
                }
                else
                {
                    GameState = GameState.InstallGame;
                }
                return;
            }
            bool isGameExeExists = await _gameLauncherService.IsGameExeExistsAsync(CurrentGameId, cancellation: cancellation);
            localGameVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId, cancellation: cancellation);
            // DX12 选项与版本无关，不等下面的版本检查：冷启动时要等好几个请求，
            // 请求失败或需要更新 / 继续下载时选项会一直不显示
            _ = CheckDX12ConfigAsync();
            if (isGameExeExists && localGameVersion != null)
            {
                GameState = GameState.StartGame;
            }
            else
            {
                GameState = GameState.ResumeDownload;
                return;
            }
            await CheckGameRunningAsync(cancellation);
            (latestGameVersion, predownloadGameVersion) = await _gameLauncherService.GetLatestGameVersionAsync(CurrentGameId, cancellation);
            if (latestGameVersion > localGameVersion)
            {
                GameState = GameState.UpdateGame;
                return;
            }
            if (predownloadGameVersion > localGameVersion)
            {
                IsPredownloadButtonEnabled = true;
                IsPredownloadFinished = await _gameLauncherService.CheckPreDownloadFinishedAsync(CurrentGameId, cancellation: cancellation);
            }
            if (GameState is not GameState.GameIsRunning)
            {
                if (await _gameLauncherService.CheckPluginUpdateAsync(CurrentGameId, cancellation: cancellation))
                {
                    GameState = GameState.UpdatePlugin;
                }
                else
                {
                    try
                    {
                        if (await _gameLauncherService.CheckGenshinHotUpdateAsync(CurrentGameId, cancellation: cancellation))
                        {
                            GameState = GameState.HotUpdate;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "CheckGenshinHotUpdateAsync");
                        if (ex.Message.Contains("CutsceneDispatchInfo"))
                        {
                            InAppToast.MainWindow?.Warning(Lang.StartGameButton_HotUpdate, Lang.GameLauncherSettingDialog_CutsceneResourceManifestIsUnavailable);
                        }
                        else
                        {
                            InAppToast.MainWindow?.Warning(Lang.GameLauncherPage_HotUpdateCheckFailed, ex.Message);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (UnloadCancellationToken.IsCancellationRequested) { }
        catch (miHoYoApiException ex) when (ex.ReturnCode is -100 or -403)
        {
            if (ex.ReturnCode is -100)
            {
                InAppToast.MainWindow?.Warning(null, Lang.InstallGameDialog_LoginExpiredPleaseLogInAgain);
            }
            if (ex.ReturnCode is -403)
            {
                InAppToast.MainWindow?.Warning(null, Lang.InstallGameDialog_CurrentAccountDoesnTHaveBetaTestingAccess);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check game version");
        }
    }


    private async Task<bool> CheckGameReservationAsync(CancellationToken cancellation)
    {
        try
        {
            var gameInfo = await _hoYoPlayService.GetGameInfoAsync(CurrentGameId, cancellation);
            if (gameInfo is null)
            {
                return false;
            }
            if (gameInfo.DisplayStatus is GameInfoDisplayStatus.LAUNCHER_GAME_DISPLAY_STATUS_RESERVATION_ENABLED)
            {
                reservationUrl = gameInfo.Reservation?.Link;
                return true;
            }
        }
        catch { }
        return false;
    }


    private async Task OpenReservationUrlAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(reservationUrl))
            {
                await Launcher.LaunchUriAsync(new(reservationUrl));
            }
        }
        catch { }
    }


    /// <summary>
    /// 检查 DX12 配置
    /// </summary>
    /// <returns></returns>
    private async Task CheckDX12ConfigAsync(CancellationToken cancellation = default)
    {
        try
        {
            // 当前预设里显式开启过 DX12 时，即使接口暂时拿不到配置也要给用户看到这个选项。
            // 注意只能认 true：null 是「跟随游戏默认」，而被误播种成 false 的预设不该被当成用户配置过。
            bool configured = CurrentLaunchProfile.EnableDX12 is true;
            // 先按上次接口的结果显示，接口慢或断网时选项也不会缺席
            IsDX12OptionAvailable = configured || AppConfig.GetDX12SwitchAvailable(CurrentGameId);

            var gameConfig = await _hoYoPlayService.GetGameConfigAsync(CurrentGameId, cancellation);
            if (gameConfig?.EnableDxSwitch is true)
            {
                DXConfig = await _hoYoPlayService.GetDXConfigAsync(CurrentGameId, GPUInfoHelper.GetGPUInfos(), cancellation);
                // 是否可切换取决于显卡：换了不支持的显卡时要能收起；自动模式由游戏自己选，不给开关
                bool available = DXConfig?.EnableDXSwitch is true && !DXConfig.AutoMode;
                AppConfig.SetDX12SwitchAvailable(CurrentGameId, available);
                IsDX12OptionAvailable = configured || available;
                if (available)
                {
                    // DX12 属于启动预设：null 表示跟随游戏本身的默认值（0.19 上游语义）
                    _enableDX12 = CurrentLaunchProfile.EnableDX12 ?? DXConfig!.UseDX12ByDefault;
                    OnPropertyChanged(nameof(EnableDX12));
                }
            }
            else
            {
                AppConfig.SetDX12SwitchAvailable(CurrentGameId, false);
                IsDX12OptionAvailable = configured;
                if (configured)
                {
                    // 按显式配置显示时勾选状态也要跟着预设走，否则会出现一个「可见但没勾」的复选框。
                    _enableDX12 = CurrentLaunchProfile.EnableDX12 is true;
                    OnPropertyChanged(nameof(EnableDX12));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check DX12 config");
        }
    }


    /// <summary>
    /// 显示 DX12 说明对话框
    /// </summary>
    private async void Hyperlink_DX12Intro_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        try
        {
            // 选项可能是按本地记录显示的，此时配置还没取到或上次请求失败了，点击时再取一次
            if (DXConfig is null)
            {
                await CheckDX12ConfigAsync();
            }
            if (DXConfig is not null)
            {
                await new DX12IntroDialog { GameDXConfig = DXConfig, XamlRoot = this.XamlRoot }.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show DX12 intro");
        }
    }


    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <returns></returns>
    private async Task LocateGameAsync()
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (DriveHelper.GetDriveType(folder) is DriveType.Network && !new Uri(folder).IsUnc)
                {
                    InAppToast.MainWindow?.Warning(null, Lang.InstallGameDialog_MappedNetworkDrivesAreNotSupportedPleaseUseANetworkSharePathStartingWithDoubleBackslashes, 0);
                }
                else
                {
                    _gameLauncherService.ChangeGameInstallPath(CurrentGameId, folder);
                    CheckGameVersion();
                    WeakReferenceMessenger.Default.Send(new GameInstallPathChangedMessage());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Locate game");
        }
    }


    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private async void Hyperlink_LocateGame_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        await LocateGameAsync();
    }


    private void OnGameInstallPathChanged(object _, GameInstallPathChangedMessage message)
    {
        CheckGameVersion();
        UpdateGameInstallTask();
    }


    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate && (message.ElapsedOver(TimeSpan.FromMinutes(10)) || message.IsCrossingHour))
            {
                CheckGameVersion();
            }
        }
        catch { }
    }


    private void OnRemovableStorageDeviceChanged(object _, RemovableStorageDeviceChangedMessage message)
    {
        try
        {
            CheckGameVersion();
        }
        catch { }
    }


    #endregion



    #region Start Game

    private System.Timers.Timer processTimer;

    /// <summary><see cref="CheckGameExited"/> 的累计次数，用来决定哪一次顺带读线程快照。</summary>
    private int _gameExitCheckCount;


    [ObservableProperty]
    private partial Process? GameProcess { get; set; }


    partial void OnGameProcessChanged(Process? oldValue, Process? newValue)
    {
        processTimer?.Stop();
        if (processTimer is null)
        {
            processTimer = new(1000);
            processTimer.Elapsed += (_, _) => CheckGameExited();
        }
        if (newValue != null)
        {
            processTimer?.Start();
            RunningGameInfo = $"{newValue.ProcessName}.exe ({newValue.Id})";
            RunningGameService.AddRuninngGame(CurrentGameBiz, newValue);
        }
        else
        {
            RunningGameInfo = null;
            _logger.LogInformation("Game process exited");
        }
    }


    public string? RunningGameInfo { get; set => SetProperty(ref field, value); }


    public string? RunningGameTime { get; set => SetProperty(ref field, value); }


    private async Task<bool> CheckGameRunningAsync(CancellationToken cancellation)
    {
        try
        {
            GameProcess = await _gameLauncherService.GetGameProcessAsync(CurrentGameId, cancellation);
            if (GameProcess != null)
            {
                GameState = GameState.GameIsRunning;
                RunningGameTime = TimeSpanToString(DateTime.Now - GameProcess.StartTime);
                _logger.LogInformation("Game is running ({name}, {pid})", GameProcess.ProcessName, GameProcess.Id);
                return true;
            }
        }
        catch { }
        return false;
    }


    /// <summary>
    /// 每秒检查当前游戏进程是否还在运行。已退出或只剩挂起残留时，把按钮从「游戏正在运行」收回来。
    /// 退出码每秒查，线程快照每 <see cref="GameLauncherService.ProcessThreadCheckInterval"/> 秒查一次。
    /// </summary>
    private void CheckGameExited()
    {
        Process? process = GameProcess;
        if (process is null)
        {
            return;
        }

        // System.Timers.Timer 的回调可能重叠，计数用 Interlocked。IsProcessEnded 自己吞掉所有异常
        bool checkThreads = System.Threading.Interlocked.Increment(ref _gameExitCheckCount) % GameLauncherService.ProcessThreadCheckInterval == 0;
        if (GameLauncherService.IsProcessEnded(process, checkThreads))
        {
            // 计时器在线程池上。RunningGameInfo / RunningGameTime 绑着 x:Bind，必须回 UI 线程再清。
            DispatcherQueue?.TryEnqueue(() => ClearGameProcess(process));
            return;
        }

        TimeSpan? elapsed = null;
        try
        {
            elapsed = DateTime.Now - process.StartTime;
        }
        catch { }

        if (elapsed is TimeSpan time)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (ReferenceEquals(GameProcess, process))
                {
                    RunningGameTime = TimeSpanToString(time);
                }
            });
        }
    }


    /// <summary>
    /// 结束这一轮「游戏正在运行」。调用方必须在 UI 线程上执行。
    /// </summary>
    /// <param name="process">发起这次检查时的进程。期间若已换成另一个进程则不动。</param>
    private void ClearGameProcess(Process process)
    {
        if (!ReferenceEquals(GameProcess, process))
        {
            return;
        }
        RunningGameTime = null;
        GameProcess = null;
        CheckGameVersion();
    }


    private static string TimeSpanToString(TimeSpan value)
    {
        return $"{value.Days * 24 + value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
    }


    [RelayCommand]
    private async Task StartGameAsync()
    {
        try
        {
            var process = await _gameLauncherService.StartGameAsync(CurrentGameId, cancellation: UnloadCancellationToken);
            if (process is not null)
            {
                GameState = GameState.GameIsRunning;
                GameProcess = process;
                WeakReferenceMessenger.Default.Send(new GameStartedMessage());
            }
        }
        catch (OperationCanceledException) when (UnloadCancellationToken.IsCancellationRequested) { }
        catch (FileNotFoundException)
        {
            CheckGameVersion();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start game");
            InAppToast.MainWindow?.Error(ex);
        }
    }


    #endregion



    #region Install Game


    private async Task InstallGameAsync()
    {
        try
        {
            if (_gameInstallTask is null)
            {
                await new InstallGameDialog { CurrentGameId = CurrentGameId, XamlRoot = this.XamlRoot, }.ShowAsync();
            }
            else
            {
                await ChangeGameInstallTaskStateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install game {GameBiz}", CurrentGameBiz);
            InAppToast.MainWindow?.Error(ex);
        }
    }


    private async Task ResumeDownloadAsync()
    {
        try
        {
            if (!Directory.Exists(GameInstallPath))
            {
                CheckGameVersion();
                return;
            }
            var task = await _gameInstallService.ResumeDownloadAsync(CurrentGameId, GameInstallPath, UnloadCancellationToken);
            if (task is not null)
            {
                _gameInstallTask = task;
                _dispatchTimer.Start();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume download {GameBiz}", CurrentGameBiz);
            InAppToast.MainWindow?.Error(ex);
        }
    }


    [RelayCommand]
    private async Task PredownloadAsync()
    {
        try
        {
            if (_gameInstallTask is null)
            {
                await new PreDownloadDialog { CurrentGameId = this.CurrentGameId, XamlRoot = this.XamlRoot }.ShowAsync();
            }
            else if (_gameInstallTask.Operation is GameInstallOperation.Predownload)
            {
                if (_gameInstallTask.State is RunnerState.Stop or RunnerState.Paused or RunnerState.Error or RunnerState.Queue)
                {
                    await _gameInstallService.ContinueTaskAsync(_gameInstallTask);
                    _dispatchTimer.Start();
                }
                else if (_gameInstallTask.State is RunnerState.Running)
                {
                    await _gameInstallService.PauseTaskAsync(_gameInstallTask);
                    _dispatchTimer.Start();
                }
                else
                {
                    // GameInstallState.Stop
                    CheckGameVersion();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, nameof(PredownloadAsync));
            if (_gameInstallTask?.Operation is GameInstallOperation.Predownload)
            {
                _gameInstallTask.State = RunnerState.Error;
                _gameInstallTask.ErrorMessage = ex.Message;
            }
            else
            {
                InAppToast.MainWindow?.Error(ex);
            }
        }
    }


    private async Task UpdateGameAsync()
    {
        try
        {
            if (localGameVersion is not null && latestGameVersion > localGameVersion)
            {
                GameInstallProgress? task = await _gameInstallService.StartUpdateAsync(CurrentGameId, GameInstallPath!, UnloadCancellationToken);
                if (task is not null)
                {
                    _gameInstallTask = task;
                    _dispatchTimer.Start();
                }
            }
            else
            {
                CheckGameVersion();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update game {GameBiz}", CurrentGameBiz);
            InAppToast.MainWindow?.Error(ex);
        }
    }


    private async Task UpdatePluginAsync()
    {
        try
        {
            if (GameState is not GameState.GameIsRunning && Directory.Exists(GameInstallPath))
            {
                GameInstallProgress? task = await _gameInstallService.StarUpdatePluginAsync(CurrentGameId, GameInstallPath!, UnloadCancellationToken);
                if (task is not null)
                {
                    _gameInstallTask = task;
                    _dispatchTimer.Start();
                }
            }
            else
            {
                CheckGameVersion();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update plugin {GameBiz}", CurrentGameBiz);
            InAppToast.MainWindow?.Error(ex);
        }
    }


    private async Task HotUpdateAsync()
    {
        try
        {
            if (GameState is not GameState.GameIsRunning && Directory.Exists(GameInstallPath))
            {
                GameInstallProgress? task = await _gameInstallService.StarHotUpdateAsync(CurrentGameId, GameInstallPath!, UnloadCancellationToken);
                if (task is not null)
                {
                    _gameInstallTask = task;
                    _dispatchTimer.Start();
                }
            }
            else
            {
                CheckGameVersion();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update plugin {GameBiz}", CurrentGameBiz);
            InAppToast.MainWindow?.Error(ex);
        }
    }


    #endregion



    #region Game Install Task


    private GameInstallProgress? _gameInstallTask;


    private async Task ChangeGameInstallTaskStateAsync()
    {
        try
        {
            if (_gameInstallTask is null)
            {
                CheckGameVersion();
            }
            else if (_gameInstallTask.Operation is not GameInstallOperation.Predownload)
            {
                if (_gameInstallTask.State is RunnerState.Stop or RunnerState.Paused or RunnerState.Error or RunnerState.Queue)
                {
                    await _gameInstallService.ContinueTaskAsync(_gameInstallTask);
                    _dispatchTimer.Start();
                }
                else if (_gameInstallTask.State is RunnerState.Running)
                {
                    await _gameInstallService.PauseTaskAsync(_gameInstallTask);
                    _dispatchTimer.Start();
                }
                else
                {
                    // GameInstallState.Stop
                    CheckGameVersion();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change game install task state {GameBiz}", CurrentGameBiz);
        }
    }


    private void UpdateGameInstallTask()
    {
        try
        {
            _gameInstallTask = _gameInstallService.GetGameInstallTask(CurrentGameId);
            if (_gameInstallTask is not null)
            {
                if (_gameInstallTask.Operation is GameInstallOperation.Predownload)
                {
                    IsPredownloadButtonEnabled = true;
                }
                _dispatchTimer.Start();
            }
        }
        catch { }
    }


    private void OnGameInstallTaskStarted(object _, GameInstallTaskStartedMessage message)
    {
        if (message.InstallTask.GameId == CurrentGameId)
        {
            _gameInstallTask = message.InstallTask;
            _dispatchTimer.Start();
        }
    }


    private void UpdateGameInstallTaskProgress(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_gameInstallTask is null)
        {
            _dispatchTimer.Stop();
            return;
        }
        try
        {
            if (_gameInstallTask.Operation is GameInstallOperation.Predownload)
            {
                Button_Predownload.UpdateGameInstallTaskState(_gameInstallTask);
            }
            else
            {
                GameState = GameState.Installing;
                Button_StartGame.UpdateGameInstallTaskState(_gameInstallTask);
            }
            if (_gameInstallTask.State is RunnerState.Error)
            {
                _dispatchTimer.Stop();
            }
            else if (_gameInstallTask.State is RunnerState.Stop or RunnerState.Finish)
            {
                _dispatchTimer.Stop();
                _gameInstallTask = null;
                CheckGameVersion();
            }
        }
        catch { }
    }


    #endregion



    #region Drop Background File


    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            Border_BackgroundDragIn.Opacity = 1;
        }
    }


    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        Border_BackgroundDragIn.Opacity = 0;
        var defer = e.GetDeferral();
        try
        {
            if ((await e.DataView.GetStorageItemsAsync()).FirstOrDefault() is StorageFile file)
            {
                string? name = await BackgroundService.ChangeCustomBackgroundFileAsync(file);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
                GameLaunchProfileStore.SetCurrentCustomBackground(CurrentGameBiz, name);
                await AppBackground.Current.UpdateBackgroundAsync();
            }
        }
        catch (COMException ex)
        {
            InAppToast.MainWindow?.Error(Lang.GameLauncherSettingDialog_CannotDecodeFile);
            _logger.LogError(ex, "Change custom background failed");
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error(Lang.GameLauncherSettingDialog_AnUnknownErrorOccurredPleaseCheckTheLogs);
            _logger.LogError(ex, "Change custom background failed");
        }
        defer.Complete();
    }


    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        Border_BackgroundDragIn.Opacity = 0;
    }


    #endregion



    #region Game Setting


    [RelayCommand]
    private async Task OpenGameLauncherSettingDialogAsync()
    {
        await new GameLauncherSettingDialog { CurrentGameId = this.CurrentGameId, XamlRoot = this.XamlRoot }.ShowAsync();
    }


    #endregion



    #region Cloud Game


    private void CheckCloudGame()
    {
        try
        {
            Process? process = CloudGameService.GetCloudGameProcess(CurrentGameId);
            if (process is not null)
            {
                RunningGameService.AddRuninngGame(CurrentGameBiz, process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check cloud game {GameBiz}", CurrentGameBiz);
        }
    }


    #endregion


}


