using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Starward.Controls;
using Starward.Features.RPC;
using Starward.Features.Update;
using Starward.Features.UrlProtocol;
using Starward.Features.ViewHost;
using Starward.Frameworks;
using Starward.Helpers;
using Starward.RPC.GameInstall;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;


namespace Starward.Features.Setting;

public sealed partial class SettingPage : PageBase
{


    private readonly ILogger<SettingPage> _logger = AppConfig.GetLogger<SettingPage>();

    private readonly RpcService _rpcService = AppConfig.GetService<RpcService>();


    public SettingPage()
    {
        this.InitializeComponent();
    }





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
        await Task.Delay(100);
        InitializeLanguageSelector();
        InitializeCloseWindowOption();
        InitializeDefaultInstallPath();
        _ = UpdateCacheSizeAsync();
        _ = GetRpcServerStateAsync();
        CheckUrlProtocol();
    }



    protected override void OnUnloaded()
    {
        FlipView_Settings.Items.Clear();
    }




    #region 版本检查



    /// <summary>
    /// 预览版
    /// </summary>
    public bool EnablePreviewRelease
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.EnablePreviewRelease = value;
            }
        }
    } = AppConfig.EnablePreviewRelease;


    /// <summary>
    /// 是最新版
    /// </summary>
    public bool IsUpdated { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 更新错误文本
    /// </summary>
    public string? UpdateErrorText { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 检查更新
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        try
        {
            IsUpdated = false;
            UpdateErrorText = null;
            var release = await AppConfig.GetService<UpdateService>().CheckUpdateAsync(true);
            if (release != null)
            {
                new UpdateWindow { NewVersion = release }.Activate();
            }
            else
            {
                IsUpdated = true;
            }
        }
        catch (Exception ex)
        {
            UpdateErrorText = ex.Message;
            _logger.LogError(ex, "Check update");
        }
    }



    #endregion




    #region 语言



    private bool _languageInitialized;


    /// <summary>
    /// 语言
    /// </summary>
    private void InitializeLanguageSelector()
    {
        try
        {
            var lang = AppConfig.Language;
            ComboBox_Language.Items.Clear();
            ComboBox_Language.Items.Add(new ComboBoxItem
            {
                Content = Lang.ResourceManager.GetString(nameof(Lang.SettingPage_FollowSystem), CultureInfo.InstalledUICulture),
                Tag = "",
            });
            ComboBox_Language.SelectedIndex = 0;
            foreach (var (Title, LangCode) in Localization.LanguageList)
            {
                var box = new ComboBoxItem
                {
                    Content = Title,
                    Tag = LangCode,
                };
                ComboBox_Language.Items.Add(box);
                if (LangCode == lang)
                {
                    ComboBox_Language.SelectedItem = box;
                }
            }
        }
        finally
        {
            _languageInitialized = true;
        }
    }



    /// <summary>
    /// 语言切换
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ComboBox_Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (ComboBox_Language.SelectedItem is ComboBoxItem item)
            {
                if (_languageInitialized)
                {
                    var lang = item.Tag as string;
                    _logger.LogInformation("Language change to {lang}", lang);
                    AppConfig.Language = lang;
                    if (string.IsNullOrWhiteSpace(lang))
                    {
                        CultureInfo.CurrentUICulture = CultureInfo.InstalledUICulture;
                    }
                    else
                    {
                        CultureInfo.CurrentUICulture = new CultureInfo(lang);
                    }
                    this.Bindings.Update();
                    WeakReferenceMessenger.Default.Send(new LanguageChangedMessage());
                    AppConfig.SaveConfiguration();
                }
            }
        }
        catch (CultureNotFoundException)
        {
            CultureInfo.CurrentUICulture = CultureInfo.InstalledUICulture;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change Language");
        }
    }



    #endregion




    #region 系统视觉效果



    /// <summary>
    /// 透明/动画效果
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private async void Hyperlink_VisualEffects_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        await Launcher.LaunchUriAsync(new Uri("ms-settings:easeofaccess-visualeffects"));
    }



    #endregion




    #region 关闭窗口选项



    private bool _closeWindowOptionInitialized;



    /// <summary>
    /// 初始化关闭窗口选项
    /// </summary>
    private void InitializeCloseWindowOption()
    {
        try
        {
            var option = AppConfig.CloseWindowOption;
            if (option is MainWindowCloseOption.Hide)
            {
                RadioButton_CloseWindowOption_Hide.IsChecked = true;
            }
            else if (option is MainWindowCloseOption.Exit)
            {
                RadioButton_CloseWindowOption_Exit.IsChecked = true;
            }
            _closeWindowOptionInitialized = true;
        }
        catch { }
    }



    /// <summary>
    /// 关闭窗口选项切换
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void RadioButton_CloseWindowOption_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            try
            {
                if (_closeWindowOptionInitialized)
                {
                    if (sender is FrameworkElement fe)
                    {
                        AppConfig.CloseWindowOption = fe.Tag switch
                        {
                            MainWindowCloseOption option => option,
                            _ => 0,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Change Close Main Window Option");
            }
        }
        catch (Exception ex)
        {

        }
    }



    #endregion




    #region 下载



    /// <summary>
    /// 默认安装文件夹
    /// </summary>
    public string? DefaultInstallPath { get; set => SetProperty(ref field, value); }



    /// <summary>
    /// 初始化默认安装路径
    /// </summary>
    private void InitializeDefaultInstallPath()
    {
        try
        {
            string? path = AppConfig.DefaultGameInstallationPath;
            if (Directory.Exists(path))
            {
                DefaultInstallPath = path;
            }
            else
            {
                AppConfig.DefaultGameInstallationPath = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get default intall path");
        }
    }



    /// <summary>
    /// 下载限速
    /// </summary>
    public int SpeedLimit
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.SpeedLimitKBPerSecond = value;
                _ = SetRateLimiterAsync(value);
            }
        }
    } = AppConfig.SpeedLimitKBPerSecond;



    private async Task SetRateLimiterAsync(int value)
    {
        try
        {
            if (RpcService.CheckRpcServerRunning())
            {
                var client = RpcService.CreateRpcClient<GameInstaller.GameInstallerClient>();
                int limit = Math.Clamp(value * 1024, 0, int.MaxValue);
                await client.SetRateLimiterAsync(new RateLimiterMessage { BytesPerSecond = limit }, deadline: DateTime.UtcNow.AddSeconds(3));
            }
        }
        catch (Exception ex)
        {

        }
    }





    /// <summary>
    /// 更改默认安装路径
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task ChangeDefaultInstallPathAsync()
    {
        try
        {
            var path = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (Directory.Exists(path))
            {
                DefaultInstallPath = path;
                AppConfig.DefaultGameInstallationPath = path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change default install path");
        }
    }



    /// <summary>
    /// 打开默认安装路径
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task OpenDefaultInstallPathAsync()
    {
        try
        {
            if (Directory.Exists(DefaultInstallPath))
            {
                await Launcher.LaunchUriAsync(new Uri(DefaultInstallPath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open default install path");
        }
    }



    /// <summary>
    /// 删除默认安装路径
    /// </summary>
    [RelayCommand]
    private void DeleteDefaultInstallPath()
    {
        DefaultInstallPath = null;
        AppConfig.DefaultGameInstallationPath = null;
    }



    #endregion




    #region 文件管理



    /// <summary>
    /// 修改数据文件夹
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task ChangeUserDataFolderAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = Lang.SettingPage_ReselectDataFolder,
                // 当前数据文件夹的位置是：
                // 想要重新选择吗？（你需要在选择前手动迁移数据文件）
                Content = $"""
                {Lang.SettingPage_TheCurrentLocationOfTheDataFolderIs}

                {AppConfig.UserDataFolder}

                {Lang.SettingPage_WouldLikeToReselectDataFolder}
                """,
                PrimaryButtonText = Lang.Common_Yes,
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result is ContentDialogResult.Primary)
            {
                AppConfig.UserDataFolder = null!;
                AppConfig.SaveConfiguration();
                AppInstance.GetCurrent().UnregisterKey();
                Process.Start(AppConfig.StarwardExecutePath);
                App.Current.Exit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change data folder");
        }
    }



    /// <summary>
    /// 打开数据文件夹
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task OpenUserDataFolderAsync()
    {
        try
        {
            if (Directory.Exists(AppConfig.UserDataFolder))
            {
                await Launcher.LaunchUriAsync(new Uri(AppConfig.UserDataFolder));
            }
        }
        catch (Exception ex)
        {

        }
    }


    /// <summary>
    /// 删除所有设置
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task DeleteAllSettingAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = Lang.SettingPage_DeleteAllSettings,
                // 删除完成后，将自动重启软件。
                Content = Lang.SettingPage_AfterDeletingTheSoftwareWillBeRestartedAutomatically,
                PrimaryButtonText = Lang.Common_Delete,
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                AppConfig.DeleteAllSettings();
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (File.Exists(exe))
                {
                    Process.Start(exe);
                    Environment.Exit(0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete all setting");
        }

    }



    #endregion




    #region Log



    /// <summary>
    /// 打开日志文件夹
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        try
        {
            if (File.Exists(AppConfig.LogFile))
            {
                var item = await StorageFile.GetFileFromPathAsync(AppConfig.LogFile);
                var options = new FolderLauncherOptions();
                options.ItemsToSelect.Add(item);
                await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(AppConfig.LogFile), options);
            }
            else
            {
                await Launcher.LaunchFolderPathAsync(Path.Combine(AppConfig.CacheFolder, "log"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open log folder");
        }
    }



    #endregion




    #region Cache



    public string LogCacheSize { get; set => SetProperty(ref field, value); } = "0.00 KB";

    public string ImageCacheSize { get; set => SetProperty(ref field, value); } = "0.00 KB";

    public string WebCacheSize { get; set => SetProperty(ref field, value); } = "0.00 KB";

    public string GameCacheSize { get; set => SetProperty(ref field, value); } = "0.00 KB";


    /// <summary>
    /// 更新缓存大小
    /// </summary>
    /// <returns></returns>
    private async Task UpdateCacheSizeAsync()
    {
        try
        {
            var local = AppConfig.CacheFolder;
            LogCacheSize = await GetFolderSizeStringAsync(Path.Combine(local, "log"));
            ImageCacheSize = await GetFolderSizeStringAsync(Path.Combine(local, "cache"));
            WebCacheSize = await GetFolderSizeStringAsync(Path.Combine(local, "webview"));
            GameCacheSize = await GetFolderSizeStringAsync(Path.Combine(local, "game"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update cache size");
        }
    }



    private static async Task<string> GetFolderSizeStringAsync(string folder) => await Task.Run(() =>
    {
        if (Directory.Exists(folder))
        {
            double size = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            if (size < (1 << 20))
            {
                return $"{size / (1 << 10):F2} KB";
            }
            else
            {
                return $"{size / (1 << 20):F2} MB";
            }
        }
        else
        {
            return "0.00 KB";
        }
    });



    /// <summary>
    /// 清除缓存
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            var local = AppConfig.CacheFolder;
            await DeleteFolderAsync(Path.Combine(local, "log"));
            await DeleteFolderAsync(Path.Combine(local, "crash"));
            await DeleteFolderAsync(Path.Combine(local, "cache"));
            await DeleteFolderAsync(Path.Combine(local, "webview"));
            await DeleteFolderAsync(Path.Combine(local, "update"));
            await DeleteFolderAsync(Path.Combine(local, "game"));
            CachedImage.ClearCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clear cache");
        }
        await UpdateCacheSizeAsync();
    }



    private async Task DeleteFolderAsync(string folder) => await Task.Run(() =>
    {
        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, true);
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete folder '{folder}'", folder);
            }
        }
    });




    #endregion




    #region URL Protocol



    [ObservableProperty]
    public bool _EnableUrlProtocol;


    partial void OnEnableUrlProtocolChanged(bool value)
    {
        try
        {
            if (value)
            {
                UrlProtocolService.RegisterProtocol();
            }
            else
            {
                UrlProtocolService.UnregisterProtocol();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enable url protocol changed");
        }
    }



    private async void CheckUrlProtocol()
    {
        try
        {
            var status = await Launcher.QueryUriSupportAsync(new Uri("starward://"), LaunchQuerySupportType.Uri);
#pragma warning disable MVVMTK0034 // Direct field reference to [ObservableProperty] backing field
            _EnableUrlProtocol = status is LaunchQuerySupportStatus.Available;
#pragma warning restore MVVMTK0034 // Direct field reference to [ObservableProperty] backing field
            OnPropertyChanged(nameof(EnableUrlProtocol));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check url protocol");
        }
    }




    [RelayCommand]
    private async Task TestUrlProtocolAsync()
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("starward://test"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test url protocol");
        }
    }


    #endregion




    #region RPC


    public bool KeepRpcServerRunningInBackground
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.KeepRpcServerRunningInBackground = value;
                SetRpcServerRunning(value);
            }
        }
    } = AppConfig.KeepRpcServerRunningInBackground;



    private void SetRpcServerRunning(bool value)
    {
        try
        {
            _rpcService.KeepRunningOnExited(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Set rpc server running");
        }
    }



    public int RPCServerProcessId { get; set => SetProperty(ref field, value); }



    private async Task GetRpcServerStateAsync()
    {
        try
        {
            RPCServerProcessId = 0;
            StackPanel_RpcState_NotRunning.Visibility = Visibility.Collapsed;
            StackPanel_RpcState_Running.Visibility = Visibility.Collapsed;
            StackPanel_RpcState_CannotConnect.Visibility = Visibility.Collapsed;
            if (RpcService.CheckRpcServerRunning())
            {
                var info = await _rpcService.GetRpcServerInfoAsync(DateTime.UtcNow.AddSeconds(3));
                RPCServerProcessId = info.ProcessId;
                StackPanel_RpcState_Running.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanel_RpcState_NotRunning.Visibility = Visibility.Visible;
            }
        }
        catch (RpcException ex) when (ex.Status is { StatusCode: StatusCode.DeadlineExceeded })
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            var process = Process.GetProcessesByName("Starward.RPC").FirstOrDefault(x => x.SessionId == sessionId);
            if (process != null)
            {
                RPCServerProcessId = process.Id;
                StackPanel_RpcState_CannotConnect.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanel_RpcState_NotRunning.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get rpc server state");
        }
    }


    [RelayCommand]
    private async Task RunRpcServerAsync()
    {
        try
        {
            await _rpcService.EnsureRpcServerRunningAsync();
            await Task.Delay(1000);
            await GetRpcServerStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run rpc server");
        }
    }



    [RelayCommand]
    private async Task StopRpcServerAsync()
    {
        try
        {
            await _rpcService.StopRpcServerAsync(DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(1000);
            await GetRpcServerStateAsync();
        }
        catch (RpcException ex) when (ex.Status is { StatusCode: StatusCode.DeadlineExceeded })
        {
            try
            {
                var p = Process.GetProcessById(RPCServerProcessId);
                p.Kill();
                await Task.Delay(1000);
                await GetRpcServerStateAsync();
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop rpc server");
        }
    }





    #endregion




    private void TextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (sender.FontSize > 12)
        {
            sender.FontSize -= 1;
        }
    }



}
