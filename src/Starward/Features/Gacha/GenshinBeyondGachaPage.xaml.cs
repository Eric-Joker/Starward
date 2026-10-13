using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Core.Gacha;
using Starward.Core.Gacha.Genshin;
using Starward.Features.GameLauncher;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;


namespace Starward.Features.Gacha;

public sealed partial class GenshinBeyondGachaPage : PageBase
{


    private readonly ILogger<GenshinBeyondGachaPage> _logger = AppConfig.GetLogger<GenshinBeyondGachaPage>();

    private readonly GenshinBeyondGachaService _gachaLogService = AppConfig.GetService<GenshinBeyondGachaService>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();



    public GenshinBeyondGachaPage()
    {
        InitializeComponent();
    }


    public ObservableCollection<long> UidList { get; set => SetProperty(ref field, value); }


    [ObservableProperty]
    public partial long? SelectUid { get; set; }
    partial void OnSelectUidChanged(long? value)
    {
        AppConfig.SetLastUidInGachaLogPage("hk4eugc", value ?? 0);
        UpdateGachaTypeStats(value);
    }




    protected override async void OnLoaded()
    {
        await Task.Delay(16);
        Initialize();
        await UpdateGachaInfoAsync();
    }



    protected override void OnUnloaded()
    {
        GachaStatsType1000 = null;
        GachaStatsType2000 = null;
        GachaItemStats = null;
    }



    private void Initialize()
    {
        try
        {
            SelectUid = null;
            UidList = new(_gachaLogService.GetUids());
            var lastUid = AppConfig.GetLastUidInGachaLogPage("hk4eugc");
            if (UidList.Contains(lastUid))
            {
                SelectUid = lastUid;
            }
            else
            {
                SelectUid = UidList.FirstOrDefault();
            }
            if (UidList.Count == 0)
            {
                StackPanel_Emoji.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize");
        }
    }


    public GenshinBeyondGachaTypeStats? GachaStatsType1000 { get; set => SetProperty(ref field, value); }

    public GenshinBeyondGachaTypeStats? GachaStatsType2000 { get; set => SetProperty(ref field, value); }

    public List<GenshinBeyondGachaItemEx>? GachaItemStats { get; set => SetProperty(ref field, value); }


    private int errorCount = 0;


    private void UpdateGachaTypeStats(long? uid)
    {
        try
        {
            GachaStatsType1000 = null;
            GachaStatsType2000 = null;

            if (uid.HasValue && uid.Value != 0)
            {
                GachaStatsType1000 = _gachaLogService.GetGachaTypeStatsType1000(uid.Value);
                GachaStatsType2000 = _gachaLogService.GetGachaTypeStatsType2000(uid.Value);
                GachaItemStats = _gachaLogService.GetGachaItemStats(uid.Value);
            }

            if (GachaStatsType1000 is null && GachaStatsType2000 is null)
            {
                StackPanel_Emoji.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanel_Emoji.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateGachaTypeStats");
        }
    }




    private async Task UpdateGachaInfoAsync()
    {
        try
        {
            await _gachaLogService.UpdateGachaInfoAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update wiki data hk4eugc");
        }
    }



    [RelayCommand]
    private async Task UpdateGachaLogAsync(string? param = null)
    {
        try
        {
            string? url = null;
            if (param is "cache")
            {
                if (SelectUid is null or 0)
                {
                    return;
                }
                url = _gachaLogService.GetGachaLogUrlByUid(SelectUid.Value);
                if (string.IsNullOrWhiteSpace(url))
                {
                    // 无法找到 uid {uid} 的已缓存 URL
                    InAppToast.MainWindow?.Warning(null, string.Format(Lang.GachaLogPage_CannotFindSavedURLOfUid, SelectUid));
                    return;
                }
            }
            else
            {
                var path = GameLauncherService.GetGameInstallPath(CurrentGameId);
                if (!Directory.Exists(path))
                {
                    // 游戏未安装
                    InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_GameNotInstalled);
                    return;
                }
                InfoBar? validatingBar = null;
                try
                {
                    // 多候选 URL 校验：优先使用仍有效的 authkey，减少误删缓存
                    validatingBar = new InfoBar
                    {
                        Severity = InfoBarSeverity.Informational,
                        Message = Lang.GachaLogPage_ValidatingGachaUrl,
                        Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush,
                        IsOpen = true,
                    };
                    InAppToast.MainWindow?.Show(validatingBar);
                    url = await _gachaLogService.GetValidatedGachaLogUrlFromWebCacheAsync(CurrentGameBiz, path);
                }
                catch (GachaApiException ex) when (ex.IsAuthkeyExpired)
                {
                    errorCount++;
                    if (errorCount > 1 && IsGachaCacheFileExists())
                    {
                        errorCount = 0;
                        InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                              Lang.GachaLogPage_AlwaysFailedToGetGachaRecords,
                                                              Lang.GachaLogPage_RestartGameAfterDeletingTheCacheFolder,
                                                              Lang.GachaLogPage_DeleteCacheFolder,
                                                              () => _ = DeleteGachaCacheFolderAsync());
                    }
                    else
                    {
                        InAppToast.MainWindow?.Warning("Authkey Timeout", Lang.GachaLogPage_PleaseOpenTheGachaRecordsPageInGameAndTryAgain);
                    }
                    return;
                }
                finally
                {
                    // 无论成功、无候选还是异常，都关掉校验条（空 webCache 时也能关掉，TZ 2698c041）
                    if (validatingBar is not null)
                    {
                        validatingBar.IsOpen = false;
                    }
                }
                if (string.IsNullOrWhiteSpace(url))
                {
                    // 无法找到 URL，请在游戏中打开抽卡记录页面
                    errorCount++;
                    if (errorCount > 2 && IsGachaCacheFileExists())
                    {
                        errorCount = 0;
                        InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                              Lang.GachaLogPage_AlwaysFailedToGetGachaRecords,
                                                              Lang.GachaLogPage_RestartGameAfterDeletingTheCacheFolder,
                                                              Lang.GachaLogPage_DeleteCacheFolder,
                                                              () => _ = DeleteGachaCacheFolderAsync());
                    }
                    else
                    {
                        InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CannotFindURL);
                    }
                    return;
                }
            }
            await UpdateGachaLogInternalAsync(url, param is "all");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update gacha log");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    private async Task UpdateGachaLogInternalAsync(string url, bool all = false)
    {
        try
        {
            var uid = await _gachaLogService.GetUidFromGachaLogUrl(url);
            var cancelSource = new CancellationTokenSource();
            var button = new Button
            {
                // 取消
                Content = Lang.Common_Cancel,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var infoBar = new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush,
                ActionButton = button,
            };
            button.Click += (_, _) =>
            {
                cancelSource.Cancel();
                // 操作已取消
                infoBar.Message = Lang.GachaLogPage_OperationCanceled;
                infoBar.ActionButton = null;
            };
            InAppToast.MainWindow?.Show(infoBar);
            var progress = new Progress<string>((str) => infoBar.Message = str);
            var newUid = await _gachaLogService.GetGachaLogAsync(url, all, GachaLanguage, progress, cancelSource.Token);
            infoBar.Title = $"Uid {newUid}";
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.ActionButton = null;
            if (SelectUid == uid)
            {
                UpdateGachaTypeStats(uid);
            }
            else
            {
                if (!UidList.Contains(uid))
                {
                    UidList.Add(uid);
                }
                SelectUid = uid;
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Get gacha log canceled");
        }
        catch (GachaApiException ex)
        {
            _logger.LogWarning("Request mihoyo api error: {error}", ex.Message);
            // 原铁 -101 绝 -1 均被 GachaApiException.IsAuthkeyExpired 覆盖
            if (ex.IsAuthkeyExpired)
            {
                // authkey timeout
                // 请在游戏中打开抽卡记录页面后再重试
                errorCount++;
                if (errorCount > 1 && IsGachaCacheFileExists())
                {
                    errorCount = 0;
                    InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                          Lang.GachaLogPage_AlwaysFailedToGetGachaRecords,
                                                          Lang.GachaLogPage_RestartGameAfterDeletingTheCacheFolder,
                                                          Lang.GachaLogPage_DeleteCacheFolder,
                                                          () => _ = DeleteGachaCacheFolderAsync());
                }
                else
                {
                    InAppToast.MainWindow?.Warning("Authkey Timeout", Lang.GachaLogPage_PleaseOpenTheGachaRecordsPageInGameAndTryAgain);
                }
            }
            else
            {
                InAppToast.MainWindow?.Warning(null, ex.Message);
            }
        }
    }



    [RelayCommand]
    private async Task InputUrlAsync()
    {
        try
        {
            var textbox = new TextBox { MinWidth = 400 };
            // 预填当前 UID 已保存的 URL，合并「更新保存的 URL」与「输入 URL」
            if (SelectUid is > 0)
            {
                var saved = _gachaLogService.GetGachaLogUrlByUid(SelectUid.Value);
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    textbox.Text = saved;
                }
            }
            var dialog = new ContentDialog
            {
                // 通过 URL 更新
                Title = Lang.GachaLogPage_InputURL,
                Content = textbox,
                // 确认
                PrimaryButtonText = Lang.Common_Confirm,
                // 取消
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var url = textbox.Text;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    await UpdateGachaLogInternalAsync(url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Input url");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    public string? GachaLanguage
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.GachaLanguage = value;
            }
        }
    } = AppConfig.GachaLanguage;



    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        try
        {
            if (SelectUid is null or 0)
            {
                return;
            }
            var url = _gachaLogService.GetGachaLogUrlByUid(SelectUid.Value);
            if (!string.IsNullOrWhiteSpace(url))
            {
                ClipboardHelper.SetText(url);
                FontIcon_CopyUrl.Glyph = "\uE8FB"; // accept
                await Task.Delay(1000);
                FontIcon_CopyUrl.Glyph = "\uE8C8";  // copy
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy url");
        }
    }



    /// <summary>
    /// 一键删除游戏 webCaches（解决 authkey timeout）。游戏运行中拒绝删除；确认后递归删除并清理本地保存的 URL。
    /// 次要按钮为「在资源管理器中打开文件夹」。
    /// </summary>
    [RelayCommand]
    private async Task DeleteGachaCacheFolderAsync()
    {
        try
        {
            var installPath = GameLauncherService.GetGameInstallPath(CurrentGameId);
            if (!Directory.Exists(installPath))
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_GameNotInstalled);
                return;
            }

            var webCachesPath = GenshinBeyondGachaClient.GetWebCachesFolderPath(CurrentGameBiz, installPath);
            // 安全：路径必须落在安装目录下的 webCaches，防止误删
            string fullInstall = Path.GetFullPath(installPath);
            string fullCaches = Path.GetFullPath(webCachesPath);
            if (!fullCaches.StartsWith(fullInstall.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !fullCaches.EndsWith($"{Path.DirectorySeparatorChar}webCaches", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Refuse to delete unexpected webCaches path: {Path}", fullCaches);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = Lang.GachaLogPage_DeleteCacheFolderConfirmTitle,
                Content = Lang.GachaLogPage_DeleteCacheFolderConfirmContent,
                PrimaryButtonText = Lang.Common_Delete,
                SecondaryButtonText = Lang.GachaLogPage_OpenCacheFolder,
                CloseButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                await OpenGachaCacheFolderInExplorerAsync(webCachesPath);
                return;
            }
            if (result is not ContentDialogResult.Primary)
            {
                return;
            }

            var process = await _gameLauncherService.GetGameProcessAsync(CurrentGameId);
            if (process is not null)
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CannotDeleteCacheWhileGameIsRunning);
                return;
            }

            if (!Directory.Exists(webCachesPath))
            {
                InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CacheFolderNotFound);
                return;
            }

            Directory.Delete(webCachesPath, recursive: true);
            _gachaLogService.DeleteSavedGachaLogUrl(SelectUid);
            errorCount = 0;
            InAppToast.MainWindow?.Success(null, Lang.GachaLogPage_CacheFolderDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete gacha cache file");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    /// <summary>
    /// 在资源管理器中打开并选中 webCaches 文件夹。
    /// </summary>
    /// <param name="webCachesPath">webCaches 完整路径。</param>
    private async Task OpenGachaCacheFolderInExplorerAsync(string webCachesPath)
    {
        if (!Directory.Exists(webCachesPath))
        {
            InAppToast.MainWindow?.Warning(null, Lang.GachaLogPage_CacheFolderNotFound);
            return;
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(webCachesPath);
        var option = new FolderLauncherOptions();
        option.ItemsToSelect.Add(folder);
        await Launcher.LaunchFolderAsync(await folder.GetParentAsync(), option);
    }



    private bool IsGachaCacheFileExists()
    {
        try
        {
            var installPath = GameLauncherService.GetGameInstallPath(CurrentGameId);
            if (Directory.Exists(installPath))
            {
                var path = GenshinBeyondGachaClient.GetGachaCacheFilePath(CurrentGameBiz, installPath);
                return File.Exists(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check gacha cache file exists");
        }
        return false;
    }



    [RelayCommand]
    private void OpenItemStatsPane()
    {
        SplitView_Content.IsPaneOpen = true;
    }


}
