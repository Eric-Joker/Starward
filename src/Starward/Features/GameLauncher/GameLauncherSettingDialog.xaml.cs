using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Display;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Background;
using Starward.Features.GameInstall;
using Starward.Features.GameSelector;
using Starward.Features.GameSetting;
using Starward.GameInstall;
using Starward.GameInstall.HotUpdate;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace Starward.Features.GameLauncher;


public sealed partial class GameLauncherSettingDialog : ContentDialogBase
{

    private readonly ILogger<GameLauncherSettingDialog> _logger = AppConfig.GetLogger<GameLauncherSettingDialog>();

    private readonly HoYoPlayService _hoyoPlayService = AppConfig.GetService<HoYoPlayService>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();

    private readonly GameInstallService _gameInstallService = AppConfig.GetService<GameInstallService>();

    private readonly BackgroundService _backgroundService = AppConfig.GetService<BackgroundService>();

    private readonly GenshinDispatchClient _genshinDispatchClient = AppConfig.GetService<GenshinDispatchClient>();


    public GameLauncherSettingDialog()
    {
        this.InitializeComponent();
    }


    public GameId CurrentGameId { get; set; }

    public GameBiz CurrentGameBiz { get; set; }



    private void FlipView_Settings_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var grid = VisualTreeHelper.GetChild(FlipView_Settings, 0);
            if (grid != null)
            {
                var count = VisualTreeHelper.GetChildrenCount(grid);
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var child = VisualTreeHelper.GetChild(grid, i);
                        if (child is Button button)
                        {
                            button.IsHitTestVisible = false;
                            button.Opacity = 0;
                        }
                        else if (child is ScrollViewer scrollViewer)
                        {
                            scrollViewer.PointerWheelChanged += (_, e) => e.Handled = true;
                        }
                    }
                }
            }
        }
        catch { }
    }


    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer?.Tag is string index && int.TryParse(index, out int target))
            {
                int steps = target - FlipView_Settings.SelectedIndex;
                if (steps > 0)
                {
                    for (int i = 0; i < steps; i++)
                    {
                        FlipView_Settings.SelectedIndex++;
                    }
                }
                else
                {
                    for (int i = 0; i < -steps; i++)
                    {
                        FlipView_Settings.SelectedIndex--;
                    }
                }
            }
        }
        catch { }
    }


    protected override async void OnLoaded()
    {
        CurrentGameBiz = CurrentGameId?.GameBiz ?? default;
        CheckCanRepairGame();
        await InitializeBasicInfoAsync();
        InitializeStartArgument();
        InitializeCustomBg();
        InitializeBannerAndPost();
        await InitializeGraphicsSettingAsync();
        await InitializeResourceManageAsync();
        // 对话框还开着的时候预设被改了或换人了，顶部那行预设名和预设级设置也要跟着变。
        WeakReferenceMessenger.Default.Register<GameLaunchProfileChangedMessage>(this, OnGameLaunchProfileChanged);
    }


    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        FlipView_Settings.Items.Clear();
        UnloadGraphicsSetting();
    }



    #region 基本信息


    public PinnedGameIcon CurrentGameBizIcon { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 安装路径
    /// </summary>
    public string? InstallPath { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 文件夹大小
    /// </summary>
    public string? GameSize { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 是否可以卸载和修复
    /// </summary>
    public bool UninstallAndRepairEnabled { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 是否显示游戏公告。属于当前预设：同一个区服的不同预设可以各自决定要不要公告栏。
    /// </summary>
    public bool EnableBannerAndPost
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                CurrentLaunchProfile.EnableBannerAndPost = value;
                GameLaunchProfileStore.Save(CurrentGameBiz);
                WeakReferenceMessenger.Default.Send(new GameAnnouncementSettingChangedMessage());
            }
        }
    }


    /// <summary>
    /// 取当前预设的"是否显示游戏公告"。预设里还没记过这一项时跟随全局开关（默认开启）。
    /// </summary>
    private void InitializeBannerAndPost()
    {
        try
        {
            EnableBannerAndPost = AppConfig.GetEnableBannerAndPost(CurrentGameBiz);
        }
        catch { }
    }

    /// <summary>
    /// 是否启用游戏公告红点
    /// </summary>
    public bool DisableGameNoticeRedHot
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.DisableGameNoticeRedHot = value;
                WeakReferenceMessenger.Default.Send(new GameAnnouncementSettingChangedMessage());
            }
        }
    } = AppConfig.DisableGameNoticeRedHot;


    private async Task InitializeBasicInfoAsync()
    {
        try
        {
            InstallPath = GameLauncherService.GetGameInstallPath(CurrentGameId, out bool storageRemoved);
            GameSize = GetSize(InstallPath);
            if (await _gameLauncherService.GetGameProcessAsync(CurrentGameId, UnloadCancellationToken) is null)
            {
                UninstallAndRepairEnabled = InstallPath != null && !storageRemoved;
            }
            else
            {
                UninstallAndRepairEnabled = false;
            }
            var gameInfo = await _hoyoPlayService.GetGameInfoAsync(CurrentGameId);
            var launcher = LauncherConfigModel.FromId(_hoyoPlayService.GetLauncherConfig(CurrentGameId)?.Id);
            if (gameInfo is not null && launcher is not null)
            {
                var icon = new PinnedGameIcon(launcher, gameInfo);
                if (gameInfo.GameServerConfigs?.FirstOrDefault(x => x.GameId == CurrentGameId.Id) is GameServerConfig serverConfig)
                {
                    icon.GameName = serverConfig.I18nName;
                }
                CurrentGameBizIcon = icon;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializeBasicInfoAsync ({biz})", CurrentGameBiz);
        }
    }


    private static string? GetSize(string? path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }
        var size = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        var gb = (double)size / (1 << 30);
        return $"{gb:F2}GB";
    }


    /// <summary>
    /// 打开游戏安装文件夹
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task OpenInstalGameFolderAsync()
    {
        try
        {
            if (Directory.Exists(InstallPath))
            {
                await Launcher.LaunchUriAsync(new Uri(InstallPath));
            }
        }
        catch { }
    }


    /// <summary>
    /// 删除游戏安装路径
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task DeleteGameInstllPathAsync()
    {
        try
        {
            _gameLauncherService.ChangeGameInstallPath(CurrentGameId, null);
            WeakReferenceMessenger.Default.Send(new GameInstallPathChangedMessage());
            await InitializeBasicInfoAsync();
            await TryStopGameInstallTaskAsync();
        }
        catch { }
    }


    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task LocateGameAsync()
    {
        try
        {
            string? previousInstallPath = InstallPath;
            string? folder = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (DriveHelper.GetDriveType(folder) is DriveType.Network && !new Uri(folder).IsUnc)
                {
                    TextBlock_NetworkDriveWarning.Visibility = Visibility.Visible;
                }
                else
                {
                    TextBlock_NetworkDriveWarning.Visibility = Visibility.Collapsed;
                    _gameLauncherService.ChangeGameInstallPath(CurrentGameId, folder);
                    await InitializeBasicInfoAsync();
                    WeakReferenceMessenger.Default.Send(new GameInstallPathChangedMessage());
                    if (previousInstallPath != folder)
                    {
                        await TryStopGameInstallTaskAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Locate game failed {GameBiz}", CurrentGameBiz);
        }
    }


    /// <summary>
    /// 检查是否可以修复游戏
    /// </summary>
    private void CheckCanRepairGame()
    {
        if (_gameInstallService.GetGameInstallTask(CurrentGameId) is GameInstallProgress task)
        {
            if (task.State is not RunnerState.Stop and not RunnerState.Finish)
            {
                Button_RepairGame.IsEnabled = false;
            }
        }
    }


    /// <summary>
    /// 修复游戏
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task RepairGameAsync()
    {
        try
        {
            if (!Directory.Exists(InstallPath))
            {
                return;
            }
            GameInstallProgress? task = await _gameInstallService.StartRepairAsync(CurrentGameId, InstallPath, UnloadCancellationToken);
            if (task is not null && task.State is not RunnerState.Stop and not RunnerState.Error)
            {
                WeakReferenceMessenger.Default.Send(new GameInstallTaskStartedMessage(task));
                Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repair game internal {GameBiz}", CurrentGameBiz);
        }
    }


    public string? UninstallError { get; set => SetProperty(ref field, value); }


    [RelayCommand]
    private void ShowUninstallGameWarning()
    {
        try
        {
            if (Directory.Exists(InstallPath))
            {
                string installPath = Path.GetFullPath(InstallPath);
                if (Path.GetPathRoot(InstallPath) == InstallPath)
                {
                    // 不能删除驱动器根目录
                    UninstallError = Lang.GameLauncherSettingDialog_CannotDeleteTheDriveRootDirectory;
                    return;
                }
                if (Directory.Exists(AppConfig.UserDataFolder))
                {
                    string userDataFolder = Path.GetFullPath(AppConfig.UserDataFolder);
                    if (userDataFolder.StartsWith(installPath))
                    {
                        // Starward 数据文件夹位于游戏文件夹内，删除游戏时会一并删除。请在设置页面修改数据文件夹位置后重试。
                        UninstallError = Lang.GameLauncherSettingDialog_UninstallGameUserDataFolderWarning;
                        return;
                    }
                }
                string baseFolder = AppContext.BaseDirectory.TrimEnd('/', '\\');
                if (baseFolder.StartsWith(installPath))
                {
                    // Starward 程序位于游戏文件夹内，删除游戏时会一并被删除。请将程序移出游戏文件夹后重试。
                    UninstallError = Lang.GameLauncherSettingDialog_UninstallGameStarwardProgramFolderWarning;
                    return;
                }
                Grid_UninstallWarning.Visibility = Visibility.Visible;
            }
            else
            {
                _ = InitializeBasicInfoAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show uninstall game warning {GameBiz}", CurrentGameBiz);
        }
    }


    [RelayCommand]
    private async Task UninstallGameAsync()
    {
        try
        {
            UninstallError = null;
            if (await _gameLauncherService.GetGameProcessAsync(CurrentGameId, UnloadCancellationToken) is not null)
            {
                UninstallError = Lang.LauncherPage_GameIsRunning;
                await InitializeBasicInfoAsync();
                return;
            }
            if (Directory.Exists(InstallPath))
            {
                if (await _gameInstallService.StartUninstallAsync(CurrentGameId, InstallPath, UnloadCancellationToken))
                {
                    _logger.LogInformation("""
                        Uninstall game finished:
                        GameId: {gameId} {gameBiz}
                        InstallPath: {installPath}
                        """, CurrentGameId.Id, CurrentGameId.GameBiz, InstallPath);
                    Grid_UninstallWarning.Visibility = Visibility.Collapsed;
                    WeakReferenceMessenger.Default.Send(new GameInstallPathChangedMessage());
                    CheckCanRepairGame();
                    await InitializeBasicInfoAsync();
                }
            }
            else
            {
                await InitializeBasicInfoAsync();
            }
        }
        catch (Exception ex)
        {
            UninstallError = ex.Message;
            _logger.LogError(ex, "Uninstall game failed {GameBiz}", CurrentGameBiz);
        }
    }


    private async Task TryStopGameInstallTaskAsync()
    {
        try
        {
            if (_gameInstallService.GetGameInstallTask(CurrentGameId) is GameInstallProgress task)
            {
                if (task.State is not RunnerState.Stop and not RunnerState.Finish)
                {
                    await _gameInstallService.StopTaskAsync(task);
                    await Task.Delay(1000);
                    CheckCanRepairGame();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Try stop game install task {GameBiz}", CurrentGameBiz);
        }
    }


    #endregion



    #region 启动参数


    /// <summary>
    /// 当前区服选中的启动预设。启动参数、DX12、自定义启动程序、自定义背景都改这个预设。
    /// </summary>
    private GameLaunchProfile CurrentLaunchProfile => GameLaunchProfileStore.GetCurrentProfile(CurrentGameBiz);


    /// <summary>
    /// 当前预设名，显示在启动参数页顶部，说明这些设置改的是哪个预设。
    /// 用带通知的属性而不是只读计算属性，否则换预设后这一行不会重新取值。
    /// </summary>
    public string? CurrentLaunchProfileName { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 命令行启动参数。属于当前预设，允许为空（不附加任何启动参数）。
    /// </summary>
    public string? StartGameArgument
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                CurrentLaunchProfile.Argument = value;
                GameLaunchProfileStore.Save(CurrentGameBiz);
            }
        }
    }

    /// <summary>
    /// 启动游戏后的操作
    /// </summary>
    public int StartGameAction
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.StartGameAction = (StartGameAction)value;
            }
        }
    } = Math.Clamp((int)AppConfig.StartGameAction, 0, 2);

    /// <summary>
    /// 是否启用第三方工具（自定义启动程序）。属于当前预设。
    /// </summary>
    public bool EnableThirdPartyTool
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                CurrentLaunchProfile.EnableThirdPartyTool = value;
                GameLaunchProfileStore.Save(CurrentGameBiz);
            }
        }
    }

    /// <summary>
    /// 第三方工具路径
    /// </summary>
    public string? ThirdPartyToolPath
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                GameLauncherService.SetThirdPartyToolPath(CurrentGameId, value);
            }
        }
    }

    /// <summary>
    /// 使用 CMD 启动游戏
    /// </summary>
    public bool StartGameWithCMD
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.StartGameWithCMD = value;
            }
        }
    } = AppConfig.StartGameWithCMD;



    private void InitializeStartArgument()
    {
        GameLaunchProfile profile = CurrentLaunchProfile;
        StartGameArgument = profile.Argument;
        EnableThirdPartyTool = profile.EnableThirdPartyTool;
        // 自定义启动程序的路径读的是当前预设的值，见 GameLauncherService.GetThirdPartyToolPath
        ThirdPartyToolPath = GameLauncherService.GetThirdPartyToolPath(CurrentGameId);
        // 当前预设可能已经换人了，顶部那行预设名要跟着当前预设重新取值。
        CurrentLaunchProfileName = profile.DisplayName;
    }


    /// <summary>
    /// 预设被改名 / 新增 / 删除 / 排序，或者当前预设被换掉之后刷新：
    /// 顶部那行预设名、命令行参数、第三方工具、自定义背景、显示游戏公告都跟着当前预设走。
    /// 和 <see cref="GameLauncherPage.OnGameLaunchProfileChanged"/> 同一个消息、同一种刷新方式。
    /// </summary>
    private void OnGameLaunchProfileChanged(object _, GameLaunchProfileChangedMessage message)
    {
        try
        {
            InitializeStartArgument();
            InitializeCustomBg();
            InitializeBannerAndPost();
        }
        catch { }
    }


    /// <summary>
    /// 修改第三方启动工具路径
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task ChangeThirdPartyPathAsync()
    {
        try
        {
            var file = await FileDialogHelper.PickSingleFileAsync(this.XamlRoot);
            if (File.Exists(file))
            {
                ThirdPartyToolPath = file;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change third party tool path ({biz})", CurrentGameBiz);
        }
    }


    /// <summary>
    /// 打开第三方工具文件夹
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task OpenThirdPartyToolFolderAsync()
    {
        try
        {
            if (File.Exists(ThirdPartyToolPath))
            {
                var folder = Path.GetDirectoryName(ThirdPartyToolPath);
                var file = await StorageFile.GetFileFromPathAsync(ThirdPartyToolPath);
                var option = new FolderLauncherOptions();
                option.ItemsToSelect.Add(file);
                await Launcher.LaunchFolderPathAsync(folder, option);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open third party tool folder {folder}", ThirdPartyToolPath);
        }
    }


    /// <summary>
    /// 删除第三方工具路径
    /// </summary>
    [RelayCommand]
    private void DeleteThirdPartyToolPath()
    {
        ThirdPartyToolPath = null;
    }


    #endregion



    #region 自定义背景


    /// <summary>
    /// 自定义背景，文件名，存储在 UserDataFolder/bg
    /// </summary>
    public string? CustomBg { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 修改背景错误信息
    /// </summary>
    public string? ChangeBgError { get; set => SetProperty(ref field, value); }


    private void InitializeCustomBg()
    {
        try
        {
            GameLaunchProfile profile = CurrentLaunchProfile;
            CustomBg = profile.CustomBg ?? AppConfig.GetCustomBg(CurrentGameId);
        }
        catch { }
    }


    /// <summary>
    /// 修改自定义背景
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task ChangeCustomBgAsync()
    {
        try
        {
            ChangeBgError = null;
            string? name = await _backgroundService.ChangeCustomBackgroundFileAsync(this.XamlRoot);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            CustomBg = name;
            // 自定义背景属于启动预设：写进当前预设，切换预设时背景跟着换
            CurrentLaunchProfile.CustomBg = name;
            GameLaunchProfileStore.Save(CurrentGameBiz);
            AppConfig.SetCustomBg(CurrentGameId, name);
            await AppBackground.Current.UpdateBackgroundAsync();
        }
        catch (COMException ex)
        {
            ChangeBgError = Lang.GameLauncherSettingDialog_CannotDecodeFile;
            _logger.LogError(ex, "Change custom background failed");
        }
        catch (Exception ex)
        {
            ChangeBgError = Lang.GameLauncherSettingDialog_AnUnknownErrorOccurredPleaseCheckTheLogs;
            _logger.LogError(ex, "Change custom background failed");
        }
    }


    /// <summary>
    /// 打开自定义背景文件
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task OpenCustomBgAsync()
    {
        try
        {
            // 0.18 的自定义背景在缓存目录、0.19 在用户数据目录：两个都找，升级上来的旧文件也能打开。
            string? path = Starward.Features.Background.BackgroundService.GetCustomBgFilePath(CustomBg);
            if (path is not null)
            {
                await Launcher.LaunchUriAsync(new Uri(path));
            }
        }
        catch { }
    }


    /// <summary>
    /// 删除自定义背景
    /// </summary>
    [RelayCommand]
    private void DeleteCustomBg()
    {
        CustomBg = null;
        CurrentLaunchProfile.CustomBg = null;
        GameLaunchProfileStore.Save(CurrentGameBiz);
        AppConfig.SetCustomBg(CurrentGameId, null);
        _ = AppBackground.Current.UpdateBackgroundAsync();
    }


    /// <summary>
    /// 视频背景音量
    /// </summary>
    public int VideoBgVolume
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(VideoBgVolumeButtonIcon));
                AppBackground.Current.VideoBgVolume = value;
            }
        }
    } = AppConfig.VideoBgVolume;


    /// <summary>
    /// 音量图标
    /// </summary>
    public string VideoBgVolumeButtonIcon => VideoBgVolume switch
    {
        > 66 => "\uE995",
        > 33 => "\uE994",
        > 1 => "\uE993",
        _ => "\uE992",
    };


    private int notMuteVolume = 100;

    /// <summary>
    /// 静音
    /// </summary>
    [RelayCommand]
    private void Mute()
    {
        if (VideoBgVolume > 0)
        {
            notMuteVolume = VideoBgVolume;
            VideoBgVolume = 0;
        }
        else
        {
            VideoBgVolume = notMuteVolume;
        }
    }


    #endregion



    #region 图形设置


    private async Task InitializeGraphicsSettingAsync()
    {
        try
        {
            if (CurrentGameId.GameBiz.Game is GameBiz.hk4e)
            {
                StackPanel_GenshinHDR.Visibility = Visibility.Visible;
                NavigationViewItem_GraphicsSetting.Visibility = Visibility.Visible;
                _displayInformation = DisplayInformation.CreateForWindowId(this.XamlRoot.GetAppWindow().Id);
                _displayInformation.AdvancedColorInfoChanged += DisplayInformation_AdvancedColorInfoChanged;
                UpdateHdrState(_displayInformation);
            }
            if (CurrentGameId.GameBiz.Game is GameBiz.hkrpg)
            {
                StackPanel_StarRailFPS.Visibility = Visibility.Visible;
                NavigationViewItem_GraphicsSetting.Visibility = Visibility.Visible;
                ComboBox_StarRailFPS.SelectedIndex = GameSettingService.GetStarRailFPSIndex(CurrentGameId.GameBiz);
                ComboBox_StarRailFPS.SelectionChanged += ComboBox_StarRailFPS_SelectionChanged;
            }
            var gameConfig = await _hoyoPlayService.GetGameConfigAsync(CurrentGameId, UnloadCancellationToken);
            if (gameConfig?.EnableDxSwitch is true)
            {
                var dxConfig = await _hoyoPlayService.GetDXConfigAsync(CurrentGameId, GPUInfoHelper.GetGPUInfos(), UnloadCancellationToken);
                if (dxConfig?.EnableDXSwitch is true && dxConfig.AutoMode)
                {
                    ComboBox_DXRenderMode.Visibility = Visibility.Visible;
                    NavigationViewItem_GraphicsSetting.Visibility = Visibility.Visible;
                    ComboBox_DXRenderMode.SelectedIndex = CurrentLaunchProfile.EnableDX12 switch
                    {
                        null => 0,
                        false => 1,
                        true => 2,
                    };
                    ComboBox_DXRenderMode.SelectionChanged += ComboBox_DXRenderMode_SelectionChanged;
                }
            }
        }
        catch { }
    }


    private void UnloadGraphicsSetting()
    {
        try
        {
            if (_displayInformation is not null)
            {
                _displayInformation.AdvancedColorInfoChanged -= DisplayInformation_AdvancedColorInfoChanged;
                _displayInformation.Dispose();
                _displayInformation = null;
            }
            ComboBox_DXRenderMode.SelectionChanged -= ComboBox_DXRenderMode_SelectionChanged;
            ComboBox_StarRailFPS.SelectionChanged -= ComboBox_StarRailFPS_SelectionChanged;
        }
        catch { }
    }



    private DisplayInformation? _displayInformation;

    public bool EnableGenshinHDR
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.EnableGenshinHDR = value;
            }
        }
    } = AppConfig.EnableGenshinHDR;

    public bool HDRNotSupported { get; set => SetProperty(ref field, value); }

    public bool HDRNotEnabled { get; set => SetProperty(ref field, value); }

    private void DisplayInformation_AdvancedColorInfoChanged(DisplayInformation sender, object args)
    {
        UpdateHdrState(sender);
    }

    private void UpdateHdrState(DisplayInformation displayInformation)
    {
        try
        {
            HDRNotEnabled = false;
            HDRNotSupported = false;
            var info = displayInformation.GetAdvancedColorInfo();
            if (!info.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange))
            {
                HDRNotSupported = true;
            }
            else if (info.CurrentAdvancedColorKind is not DisplayAdvancedColorKind.HighDynamicRange)
            {
                HDRNotEnabled = true;
            }
        }
        catch { }
    }



    private void ComboBox_StarRailFPS_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            GameSettingService.SetStarRailFPSIndex(CurrentGameId.GameBiz, ComboBox_StarRailFPS.SelectedIndex);
        }
        catch { }
    }



    private void ComboBox_DXRenderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // DX12 属于启动预设：null 表示跟随游戏本身的默认渲染模式
        CurrentLaunchProfile.EnableDX12 = ComboBox_DXRenderMode.SelectedIndex switch
        {
            1 => false,
            2 => true,
            _ => null,
        };
        GameLaunchProfileStore.Save(CurrentGameBiz);
    }


    #endregion



    #region 资源管理



    public bool EnableCutsceneManage
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.SetEnableCutsceneManage(CurrentGameId, value);
            }
        }
    }

    public string? ResourceManageError { get; set => SetProperty(ref field, value); }

    public string CutsceneSizeText { get; set => SetProperty(ref field, value); }



    private async Task InitializeResourceManageAsync()
    {
        const double GB = 1 << 30;
        try
        {
            if (CurrentGameId.GameBiz.Game is not GameBiz.hk4e)
            {
                return;
            }
            if (InstallPath is null)
            {
                return;
            }
            var launcher = _hoyoPlayService.GetLauncherConfig(CurrentGameId);
            if (launcher is null || launcher.IsBeta)
            {
                return;
            }
            EnableCutsceneManage = AppConfig.GetEnableCutsceneManage(CurrentGameId);
            NavigationViewItem_ResourceManagement.Visibility = Visibility.Visible;

            var branch = await _hoyoPlayService.GetGameBranchAsync(CurrentGameId, UnloadCancellationToken);
            if (branch is not null)
            {
                var collection = await _genshinDispatchClient.GetCutsceneCollectionAsync(CultureInfo.CurrentUICulture.Name, UnloadCancellationToken);
                var dispatchInfo = collection.Dispatch.GetValueOrDefault($"{(CurrentGameId.GameBiz.IsChinaServer() ? "CN" : "OS")}RELWin{branch.Main.Tag}");
                if (dispatchInfo is not null)
                {
                    long size = dispatchInfo.Cutscene.Sum(x => x.Size);
                    long existSize = 0;
                    long count = 0;
                    foreach (CutsceneInfo item in dispatchInfo.Cutscene)
                    {
                        string path = Path.Join(InstallPath, item.Path);
                        if (File.Exists(path))
                        {
                            existSize += item.Size;
                            count++;
                        }
                    }
                    CutsceneSizeText = $"{(existSize) / GB:F2}/{size / GB:F2} GB";
                    StackPanel_CutsceneSize.Visibility = Visibility.Visible;
                    if (_gameInstallService.GetGameInstallTask(CurrentGameId) is null && await _gameLauncherService.GetGameProcessAsync(CurrentGameId, UnloadCancellationToken) is null)
                    {
                        Button_ManageResource.IsEnabled = true;
                        TextBlock_GameOrInstallRunning.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        Button_ManageResource.IsEnabled = false;
                        TextBlock_GameOrInstallRunning.Visibility = Visibility.Visible;
                    }
                    Button_ManageResource.Visibility = Visibility.Visible;
                }
                else
                {
                    ResourceManageError = Lang.GameLauncherSettingDialog_CutsceneResourceManifestIsUnavailable;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializeResourceManageAsync");
            ResourceManageError = ex.Message;
        }
    }



    [RelayCommand]
    private void OpenCutsceneWindow()
    {
        new CutsceneResourceWindow() { CurrentGameId = CurrentGameId }.Show();
    }




    #endregion


}
