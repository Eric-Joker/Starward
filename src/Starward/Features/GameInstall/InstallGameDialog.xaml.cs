using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using Starward.Features.HoYoPlay;
using Starward.Helpers;
using Starward.RPC.GameInstall;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace Starward.Features.GameInstall;

[INotifyPropertyChanged]
public sealed partial class InstallGameDialog : ContentDialog
{

    private const double GB = 1 << 30;


    private readonly ILogger<InstallGameDialog> _logger = AppConfig.GetLogger<InstallGameDialog>();

    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();

    private readonly GameInstallService _gameInstallService = AppConfig.GetService<GameInstallService>();


    public InstallGameDialog()
    {
        this.InitializeComponent();
        this.Loaded += InstallGameDialog_Loaded;
        this.Unloaded += InstallGameDialog_Unloaded;
    }



    public GameId CurrentGameId { get; set; }



    private void InstallGameDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (CurrentGameId is null)
        {
            _logger.LogWarning("CurrentGameId is null.");
            this.Hide();
            return;
        }
        SetDefaultInstallationPath();
        _ = GetGamePackageAsync();
    }



    private void InstallGameDialog_Unloaded(object sender, RoutedEventArgs e)
    {
        Segmented_SelectLanguage.SelectionChanged -= Segmented_SelectLanguage_SelectionChanged;
        Segmented_SelectLanguage.Items.Clear();
    }



    private void SetDefaultInstallationPath()
    {
        try
        {
            string? defaultFolder = AppConfig.DefaultGameInstallationPath;
            if (Directory.Exists(defaultFolder))
            {
                SetInstallationPath(Path.GetFullPath(Path.Combine(defaultFolder, CurrentGameId.GameBiz)));
                return;
            }
            string baseFolder = "";
            if (AppConfig.IsAppInRemovableStorage)
            {
                if (File.Exists(AppConfig.StarwardPortableLauncherExecutePath))
                {
                    baseFolder = Path.Combine(Path.GetDirectoryName(AppConfig.StarwardPortableLauncherExecutePath)!, "Games");
                }
                else
                {
                    baseFolder = Path.Combine(Path.GetDirectoryName(AppConfig.StarwardExecutePath)!, "Games");
                }
            }
            else
            {
                string? defaultPath = AppConfig.DefaultGameInstallationPath;
                if (Directory.Exists(defaultPath))
                {
                    baseFolder = defaultPath;
                }
                else
                {
                    baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "miHoYo");
                }
            }
            string target = Path.Combine(baseFolder, CurrentGameId.GameBiz);
            if (Path.IsPathFullyQualified(target))
            {
                SetInstallationPath(Path.GetFullPath(target));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Set default install path.");
        }
    }



    private async Task GetGamePackageAsync()
    {
        try
        {
            GameConfig? config = await _hoYoPlayService.GetGameConfigAsync(CurrentGameId);
            if (config is null)
            {
                _logger.LogWarning("GameConfig of ({GameBiz}) is null.", CurrentGameId.GameBiz);
                this.Hide();
                return;
            }
            if (!string.IsNullOrWhiteSpace(config.AudioPackageScanDir))
            {
                _needAudioPackage = true;
                Segmented_SelectLanguage.Visibility = Visibility.Visible;
                SetDefaultAudioPackage();
            }
            if (GameFeatureConfig.FromGameId(CurrentGameId).SupportHardLink)
            {
                StackPanel_HardLink.Visibility = Visibility.Visible;
            }
            if (config.DefaultDownloadMode is DownloadMode.DOWNLOAD_MODE_CHUNK)
            {
                var branch = await _hoYoPlayService.GetGameBranchAsync(CurrentGameId);
                if (branch is not null)
                {
                    _gameSophonChunkBuild = await _hoYoPlayService.GetGameSophonChunkBuildAsync(branch, branch.Main);
                    // 基础资源只在 Chunk 模式提供（官方同样如此），压缩包没有按分类拆分
                    if (_gameSophonChunkBuild is not null && GameScenarioPackage.IsSupported(config, branch.Main))
                    {
                        InitializeScenarioPackage(config, branch.Main);
                    }
                }
            }
            if (_gameSophonChunkBuild is null)
            {
                _gamePackage = await _hoYoPlayService.GetGamePackageAsync(CurrentGameId);
            }
            ComputePackageSize();
            CheckCanStartInstallation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game package.");
        }
    }



    private void SetDefaultAudioPackage()
    {
        if (_needAudioPackage)
        {
            Segmented_SelectLanguage.SelectedIndex = CultureInfo.CurrentUICulture.Name[..2] switch
            {
                "zh" => 0,
                "en" => 1,
                "ja" => 2,
                "ko" => 3,
                _ => 2,
            };
        }
    }



    /// <summary>
    /// 显示完整资源 / 基础资源选项，默认选接口标「推荐」的那个（通常为完整资源，与官方一致）
    /// </summary>
    /// <param name="config">游戏配置，提供两个选项的说明与是否标「推荐」</param>
    /// <param name="package">要安装的分支，提供各分类属于哪个资源场景</param>
    private void InitializeScenarioPackage(GameConfig config, GameBranchPackage package)
    {
        _scenarioPackageSupported = true;
        _scenarioPackageInfo = config.ScenarioPackageInfo;
        _fullOnlyMatchingFields = GameScenarioPackage.GetFullOnlyMatchingFields(package);
        StackPanel_ScenarioPackage.Visibility = Visibility.Visible;
        // 接口标「推荐」的是哪个就默认选哪个，没标时默认完整资源（与官方一致）
        if (config.EnableFullPackageRecommend)
        {
            RadioButton_FullPackage.IsChecked = true;
        }
        else
        {
            RadioButton_BasePackage.IsChecked = true;
        }
    }



    /// <summary>
    /// 切换完整资源 / 基础资源
    /// </summary>
    private void RadioButton_ScenarioPackage_Checked(object sender, RoutedEventArgs e)
    {
        string? description = SelectedPackageType is GameScenarioPackageType.Base
            ? _scenarioPackageInfo?.BasePackageDescription
            : _scenarioPackageInfo?.FullPackageDescription;
        ScenarioPackageDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ComputePackageSize();
        CheckCanStartInstallation();
    }



    /// <summary>
    /// 当前选择的资源场景；不区分资源场景的游戏为 <see cref="GameScenarioPackageType.Unknown"/>
    /// </summary>
    private GameScenarioPackageType SelectedPackageType => !_scenarioPackageSupported
        ? GameScenarioPackageType.Unknown
        : RadioButton_BasePackage.IsChecked is true ? GameScenarioPackageType.Base : GameScenarioPackageType.Full;



    private bool _needAudioPackage;


    private AudioLanguage _audioLanguage;


    private GamePackage? _gamePackage;


    private GameSophonChunkBuild? _gameSophonChunkBuild;


    /// <summary>
    /// 是否提供完整资源 / 基础资源选项
    /// </summary>
    private bool _scenarioPackageSupported;


    private GameScenarioPackageInfo? _scenarioPackageInfo;


    /// <summary>
    /// 只属于完整资源的分类，选基础资源时不下载
    /// </summary>
    private HashSet<string> _fullOnlyMatchingFields = [];


    private string _selectPath;


    public string InstallationPath { get; set => SetProperty(ref field, value); }


    public string? ErrorMessage { get; set => SetProperty(ref field, value); }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnzipSpaceText))]
    public partial long UnzipSpaceBytes { get; set; }

    public string UnzipSpaceText => UnzipSpaceBytes == 0 ? "..." : $"{UnzipSpaceBytes / GB:F2} GB";


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableSpaceText))]
    public partial long AvailableSpaceBytes { get; set; }

    public string AvailableSpaceText => AvailableSpaceBytes == 0 ? "..." : $"{AvailableSpaceBytes / GB:F2} GB";


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPackageSizeText))]
    public partial long FullPackageSizeBytes { get; set; }

    public string FullPackageSizeText => FullPackageSizeBytes == 0 ? "..." : $"{FullPackageSizeBytes / GB:F2} GB";


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BasePackageSizeText))]
    public partial long BasePackageSizeBytes { get; set; }

    public string BasePackageSizeText => BasePackageSizeBytes == 0 ? "..." : $"{BasePackageSizeBytes / GB:F2} GB";


    /// <summary>
    /// 所选资源场景的说明，没有时隐藏
    /// </summary>
    public string? ScenarioPackageDescription { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 完整资源选项的名称，文案来自官方接口（随区服语言），接口没给时用英文兜底
    /// </summary>
    public string FullPackageName => string.IsNullOrWhiteSpace(_scenarioPackageInfo?.FullPackageName) ? "Full Resources" : _scenarioPackageInfo.FullPackageName;


    /// <summary>
    /// 基础资源选项的名称，文案来自官方接口（随区服语言），接口没给时用英文兜底
    /// </summary>
    public string BasePackageName => string.IsNullOrWhiteSpace(_scenarioPackageInfo?.BasePackageName) ? "Base Resources" : _scenarioPackageInfo.BasePackageName;


    [ObservableProperty]
    public partial bool AutomaticallyCreateSubfolderForInstall { get; set; } = AppConfig.AutomaticallyCreateSubfolderForInstall;
    partial void OnAutomaticallyCreateSubfolderForInstallChanged(bool value)
    {
        AppConfig.AutomaticallyCreateSubfolderForInstall = value;
        if (!string.IsNullOrWhiteSpace(_selectPath))
        {
            if (value)
            {
                SetInstallationPath(Path.Combine(_selectPath, CurrentGameId.GameBiz));
            }
            else
            {
                SetInstallationPath(_selectPath);
            }
        }
    }



    /// <summary>
    /// 按所选资源场景与语音统计解压所需空间，以及两个资源选项各自的大小
    /// </summary>
    private void ComputePackageSize()
    {
        try
        {
            long size = 0;
            List<string?> langs = Segmented_SelectLanguage.SelectedItems.Cast<SegmentedItem>().Select(x => x.Tag as string).ToList();
            if (_gamePackage is not null)
            {
                size += _gamePackage.Main.Major!.GamePackages.Sum(x => x.DecompressedSize);
                foreach (string? lang in langs)
                {
                    if (_gamePackage.Main.Major.AudioPackages.FirstOrDefault(x => x.Language == lang) is GamePackageFile gamePackageFile)
                    {
                        size += gamePackageFile.DecompressedSize;
                    }
                }
            }
            else if (_gameSophonChunkBuild is not null)
            {
                bool isBase = SelectedPackageType is GameScenarioPackageType.Base;
                long fullSize = 0, baseSize = 0;
                foreach (GameSophonChunkManifest manifest in _gameSophonChunkBuild.Manifests)
                {
                    if (manifest.MatchingField.Length is 5 or 10 && manifest.MatchingField.Contains('-'))
                    {
                        // 跳过语音包 zh-cn or mini-zh-cn
                        continue;
                    }
                    bool fullOnly = _fullOnlyMatchingFields.Contains(manifest.MatchingField);
                    fullSize += manifest.Stats.CompressedSize;
                    if (!fullOnly)
                    {
                        baseSize += manifest.Stats.CompressedSize;
                    }
                    if (!(isBase && fullOnly))
                    {
                        size += manifest.Stats.UncompressedSize;
                    }
                }
                foreach (string? lang in langs)
                {
                    if (_gameSophonChunkBuild.Manifests.FirstOrDefault(x => x.MatchingField == lang) is GameSophonChunkManifest audioManifest)
                    {
                        size += audioManifest.Stats.UncompressedSize;
                    }
                }
                FullPackageSizeBytes = fullSize;
                BasePackageSizeBytes = baseSize;
            }
            UnzipSpaceBytes = size;
            if (AvailableSpaceBytes > 0 && UnzipSpaceBytes > AvailableSpaceBytes)
            {
                TextBlock_AvailableSpace.Foreground = App.Current.Resources["SystemFillColorCautionBrush"] as Brush;
            }
            else
            {
                TextBlock_AvailableSpace.Foreground = App.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compute package size.");
        }
    }



    private void CheckCanStartInstallation()
    {
        try
        {
            ErrorMessage = null;
            Button_StartInstallation.IsEnabled = false;
            if (_gamePackage is not null || _gameSophonChunkBuild is not null)
            {
                if (DriveHelper.GetDriveType(InstallationPath) is DriveType.Network && !new Uri(InstallationPath).IsUnc)
                {
                    ErrorMessage = Lang.InstallGameDialog_MappedNetworkDrivesAreNotSupportedPleaseUseANetworkSharePathStartingWithDoubleBackslashes;
                }
                else if (Path.GetPathRoot(InstallationPath) == InstallationPath)
                {
                    ErrorMessage = Lang.LauncherPage_PleaseDoNotSelectTheRootDirectoryOfADrive;
                }
                else if (Path.IsPathFullyQualified(InstallationPath))
                {
                    if (!(_needAudioPackage ^ Segmented_SelectLanguage.SelectedItems.Count > 0))
                    {
                        Button_StartInstallation.IsEnabled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check can start installation.");
        }
    }



    [RelayCommand]
    private async Task ChangeInstallationPathAsync()
    {
        try
        {
            string? path = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (Directory.Exists(path))
            {
                _selectPath = path;
                if (AutomaticallyCreateSubfolderForInstall)
                {
                    path = Path.Combine(path, CurrentGameId.GameBiz);
                }
                SetInstallationPath(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change installation path.");
        }
    }



    private void SetInstallationPath(string path)
    {
        try
        {
            TextBlock_InstallationPath.FontSize = 14;
            InstallationPath = path;
            AvailableSpaceBytes = DriveHelper.GetDriveAvailableSpace(path);
            CheckCanStartInstallation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Set installation path.");
        }
    }



    private void Segmented_SelectLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _audioLanguage = AudioLanguage.None;
        foreach (SegmentedItem item in Segmented_SelectLanguage.SelectedItems.Cast<SegmentedItem>())
        {
            if (item.Tag is string lang)
            {
                _audioLanguage |= lang switch
                {
                    "zh-cn" => AudioLanguage.Chinese,
                    "en-us" => AudioLanguage.English,
                    "ja-jp" => AudioLanguage.Japanese,
                    "ko-kr" => AudioLanguage.Korean,
                    _ => AudioLanguage.None,
                };
            }
        }

        ComputePackageSize();
        CheckCanStartInstallation();
    }



    [RelayCommand]
    private async Task StartInstallationAsync()
    {
        try
        {
            GameInstallContext? task = await _gameInstallService.StartInstallAsync(CurrentGameId, InstallationPath, _audioLanguage, SelectedPackageType);
            if (task is not null && task.State is not GameInstallState.Stop and not GameInstallState.Error)
            {
                GameLauncherService.ChangeGameInstallPath(CurrentGameId, InstallationPath);
                WeakReferenceMessenger.Default.Send(new GameInstallTaskStartedMessage(task));
                if (_selectPath is not null && InstallationPath.EndsWith(CurrentGameId.GameBiz))
                {
                    AppConfig.DefaultGameInstallationPath = _selectPath;
                }
                Close();
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start installation.");
        }
    }




    [RelayCommand]
    private void Close()
    {
        this.Hide();
    }



    private void TextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (sender.FontSize > 12)
        {
            sender.FontSize--;
        }
    }



}
