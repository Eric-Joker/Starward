using Dapper;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Database;
using Starward.Features.GameLauncher;
using Starward.Features.ViewHost;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Starward;

public static partial class AppConfig
{


    #region Static Setting


    public static string? LastAppVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static bool EnablePreviewRelease
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static string? IgnoreVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 更新完成后自动重启
    /// </summary>
    public static bool AutoRestartWhenUpdateFinished
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 更新完成后显示更新内容
    /// </summary>
    public static bool ShowUpdateContentAfterUpdateRestart
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 主窗口关闭选项，隐藏/退出
    /// </summary>
    public static MainWindowCloseOption CloseWindowOption
    {
        get => GetValue<MainWindowCloseOption>();
        set => SetValue(value);
    }

    public static string? EnabledLauncherIds
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static string? ExtraLauncherConfigModel
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? CurrentGameId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static bool ShownFindGamesTip
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool EnableBannerAndPost
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static string? GachaLanguage
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? AccentColor
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static int VideoBgVolume
    {
        get => Math.Clamp(GetValue(0), 0, 100);
        set => SetValue(value);
    }

    public static bool AcceptHoyolabToolboxAgreement
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool HoyolabToolboxPaneOpen
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static bool DisableGameNoticeRedHot
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static StartGameAction StartGameAction
    {
        get => GetValue<StartGameAction>();
        set => SetValue(value);
    }

    public static int SpeedLimitKBPerSecond
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 崩坏3国际服多区服选项
    /// </summary>
    public static string? LastGameIdOfBH3Global
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 启用硬链接
    /// </summary>
    public static bool EnableHardLink
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 原神HDR
    /// </summary>
    public static bool EnableGenshinHDR
    {
        get => GetValue(false);
        set => SetValue(value);
    }

    /// <summary>
    /// 截图文件夹
    /// </summary>
    public static string? ScreenshotFolder
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 显示主窗口快捷键
    /// </summary>
    public static string? ShowMainWindowHotkey
    {
        // Alt + S
        get => GetValue("1+83");
        set => SetValue(value);
    }

    /// <summary>
    /// 截图快捷键
    /// </summary>
    public static string? ScreenshotCaptureHotkey
    {
        // Alt + D
        get => GetValue("1+68");
        set => SetValue(value);
    }

    /// <summary>
    /// 手柄控制
    /// </summary>
    public static bool EnableGamepadSimulateInput
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static int GamepadGuideButtonMode
    {
        get => GetValue<int>();
        set => SetValue(value);
    }

    /// <summary>
    /// GameBar 引导键当前是否被本程序接管（即注册表里的 <c>UseNexusForGameBarEnabled=0</c> 是我们写的）。
    /// 退出与下次启动据此判断该不该还原，避免把用户自己在 GameBar 里关掉的引导键又打开；
    /// 进程被强杀或崩溃会让它和那个 0 一起残留，由启动时的自愈还原。
    /// </summary>
    public static bool GamepadGuideButtonTakenOver
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static string? GamepadShareButtonMapKeys
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? GamepadGuideButtonMapKeys
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static int GamepadShareButtonMode
    {
        get => GetValue<int>();
        set => SetValue(value);
    }

    public static bool AutoConvertScreenshotToSDR
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static bool AutoCopyScreenshotToClipboard
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 截图链路色彩管理（HDR 模式始终启用）
    /// </summary>
    public static bool EnableScreenshotColorManagement
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 0: PNG, 1: AVIF, 2: JPEG XL
    /// </summary>
    public static int ScreenCaptureSavedFormat
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 0: Middle, 1: High, 2: Lossless
    /// </summary>
    public static int ScreenCaptureEncodeQuality
    {
        get => GetValue(1);
        set => SetValue(value);
    }

    public static bool EnableGamepadController
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 使用 CMD 启动游戏 <see href="https://github.com/Scighost/Starward/issues/1634"/>
    /// </summary>
    public static bool StartGameWithCMD
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool AuthLoginGameByPassportAccount
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static int PlayTimeDayStartHour
    {
        get => GetValue<int>();
        set => SetValue(value);
    }

    public static long LastUgcGachaUid
    {
        get => GetValue<long>();
        set => SetValue(value);
    }

    public static string? AndroidDeviceInfo
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    #endregion



    #region Dynamic Setting


    public static long GetLastPassportUserAid(ServerRegion region)
    {
        return GetValue<long>(default, "LastPassportUserAid", region.ToString());
    }

    public static void SetLastPassportUserAid(ServerRegion region, long value)
    {
        SetValue(value, "LastPassportUserAid", region.ToString());
    }


    public static string? GetGameBackground(GameId gameId)
    {
        return GetValue<string>(default, "GameBackground", gameId.ToString());
    }

    public static void SetGameBackground(GameId gameId, string? value)
    {
        SetValue(value, "GameBackground", gameId.ToString());
    }


    public static string? GetBg(GameId? gameId)
    {
        // 冷启动还没有当前游戏时调用方会传 null，这里必须当心：gameId.ToString() 会直接抛 NRE。
        return gameId is null ? null : GetValue<string>(default, "Bg", gameId.ToString());
    }

    public static void SetBg(GameId gameId, string? value)
    {
        SetValue(value, "Bg", gameId.ToString());
    }


    public static string? GetCustomBg(GameId? gameId)
    {
        return gameId is null ? null : GetValue<string>(default, "CustomBg", gameId.ToString());
    }

    public static void SetCustomBg(GameId gameId, string? value)
    {
        SetValue(value, "CustomBg", gameId.ToString());
    }


    public static bool GetEnableThirdPartyTool(GameId gameId)
    {
        return GetValue<bool>(default, "EnableThirdPartyTool", gameId.ToString());
    }

    public static void SetEnableThirdPartyTool(GameId gameId, bool value)
    {
        SetValue(value, "EnableThirdPartyTool", gameId.ToString());
    }


    public static string? GetThirdPartyToolPath(GameId gameId)
    {
        return GetValue<string>(default, "ThirdPartyToolPath", gameId.ToString());
    }

    public static void SetThirdPartyToolPath(GameId gameId, string? value)
    {
        SetValue(value, "ThirdPartyToolPath", gameId.ToString());
    }


    public static string? GetStartArgument(GameId gameId)
    {
        return GetValue<string>(default, "StartArgument", gameId.ToString());
    }

    public static void SetStartArgument(GameId gameId, string? value)
    {
        SetValue(value, "StartArgument", gameId.ToString());
    }


    public static long GetLastUidInGachaLogPage(GameBiz game)
    {
        return GetValue<long>(default, "LastGachaUid", game.Game);
    }

    public static void SetLastUidInGachaLogPage(GameBiz game, long value)
    {
        SetValue(value, "LastGachaUid", game.Game);
    }


    public static string? GetDisplayGachaBanners(GameBiz game)
    {
        return GetValue<string>(default, "DisplayGachaBanners", game.Game);
    }

    public static void SetDisplayGachaBanners(GameBiz game, string value)
    {
        SetValue(value, "DisplayGachaBanners", game.Game);
    }


    /// <summary>
    /// 外部截图文件夹
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public static string? GetExternalScreenshotFolder(GameId gameId)
    {
        return GetValue<string>(default, "ExternalScreenshotFolder", gameId.ToString());
    }

    /// <summary>
    /// 外部截图文件夹
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="value"></param>
    public static void SetExternalScreenshotFolder(GameId gameId, string? value)
    {
        SetValue(value, "ExternalScreenshotFolder", gameId.ToString());
    }


    /// <summary>
    /// 启用 DX12
    /// </summary>
    public static bool? GetEnableDX12(GameId gameId)
    {
        // 三态：null = 没记过（跟随游戏自己的默认值），false = 用户显式选了 DX11。
        // 以前这里用 GetValue<bool>，缺键会被读成 false，等于把「没记过」当成「显式 DX11」。
        return GetValue<bool?>((bool?)null, "EnableDX12", gameId.ToString());
    }

    /// <summary>
    /// 启用 DX12
    /// </summary>
    public static void SetEnableDX12(GameId gameId, bool? value)
    {
        SetValue(value, "EnableDX12", gameId.ToString());
    }


    public static bool GetEnableCutsceneManage(GameId gameId)
    {
        return GetValue<bool>(default, "EnableCutsceneManage", gameId.ToString());
    }

    public static void SetEnableCutsceneManage(GameId gameId, bool value)
    {
        SetValue(value, "EnableCutsceneManage", gameId.ToString());
    }


    public static long GetLastSelectGameRecordRole(GameBiz gameBiz)
    {
        return GetValue<long>(default, "LastSelectGameRecordRole", gameBiz);
    }

    public static void SetLastSelectGameRecordRole(GameBiz gameBiz, long value)
    {
        SetValue(value, "LastSelectGameRecordRole", gameBiz);
    }


    #endregion



    #region Obsolete Setting


    [Obsolete("Obsolete Setting", true)]
    public static bool IgnoreRunningGame
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool ShowNoviceGacha
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool ShowChronicledWish
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool UseOneBg
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool EnableSystemTrayIcon
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool ExitWhenClosing
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    [Obsolete("Obsolete Setting", true)]
    public static bool UseSystemThemeColor
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool EnableNavigationViewLeftCompact
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 游戏账号切换
    /// </summary>
    [Obsolete("Obsolete Setting", true)]
    public static bool EnableGameAccountSwitcher
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 当前选择的游戏区服
    /// </summary>
    [Obsolete("Obsolete Setting", true)]
    public static GameBiz CurrentGameBiz
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static string? SelectedGameBizs
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 固定待选择的游戏区服图标
    /// </summary>
    [Obsolete("Obsolete Setting", true)]
    public static bool IsGameBizSelectorPinned
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 缓存的游戏信息 <see cref="GameInfo"/>
    /// </summary>
    [Obsolete("Obsolete Setting", true)]
    public static string? CachedGameInfo
    {
        get => DatabaseService.GetValue<string>(nameof(CachedGameInfo), out _, default);
        set => DatabaseService.SetValue(nameof(CachedGameInfo), value);
    }

    /// <summary>
    /// 保持 RPC 服务在后台运行
    /// </summary>
    [Obsolete("Obsolete Setting", true)]
    public static bool KeepRpcServerRunningInBackground
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 安装游戏时自动创建子文件夹
    /// </summary>
    [Obsolete("Obsolete Setting", true)]
    public static bool AutomaticallyCreateSubfolderForInstall
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static string? DefaultGameInstallationPath
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static string? HyperionDeviceId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static string? HyperionDeviceFp
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static DateTimeOffset HyperionDeviceFpLastUpdateTime
    {
        get => GetValue<DateTimeOffset>();
        set => SetValue(value);
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool GetUseVersionPoster(GameId gameId)
    {
        return GetValue<bool>(default, "UseVersionPoster", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetUseVersionPoster(GameId gameId, bool value)
    {
        SetValue(value, "UseVersionPoster", gameId.ToString());
    }


    [Obsolete("Obsolete Setting", true)]
    public static string? GetVersionPoster(GameId gameId)
    {
        return GetValue<string>(default, "VersionPoster", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetVersionPoster(GameId gameId, string? value)
    {
        SetValue(value, "VersionPoster", gameId.ToString());
    }


    [Obsolete("Obsolete Setting", true)]
    public static bool GetEnableCustomBg(GameId gameId)
    {
        return GetValue<bool>(default, "EnableCustomBg", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetEnableCustomBg(GameId gameId, bool value)
    {
        SetValue(value, "EnableCustomBg", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static string? GetGameInstallPath(GameId gameId)
    {
        return GetValue<string>(default, "InstallPath", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetGameInstallPath(GameId gameId, string? value)
    {
        SetValue(value, "InstallPath", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static bool GetGameInstallPathRemovable(GameId gameId)
    {
        return GetValue<bool>(default, "InstallPathRemovable", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetGameInstallPathRemovable(GameId gameId, bool value)
    {
        SetValue(value, "InstallPathRemovable", gameId.ToString());
    }


    /// <summary>
    /// 无边框窗口
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    [Obsolete("Obsolete Setting", true)]
    public static bool GetUsePopupWindow(GameId gameId)
    {
        return GetValue<bool>(false, "UsePopupWindow", gameId.ToString());
    }

    /// <summary>
    /// 无边框窗口
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="value"></param>
    [Obsolete("Obsolete Setting", true)]
    public static void SetUsePopupWindow(GameId gameId, bool value)
    {
        SetValue(value, "UsePopupWindow", gameId.ToString());
    }


    [Obsolete("Obsolete Setting", true)]
    public static GameBiz GetLastRegionOfGame(GameBiz game)
    {
        return GetValue<GameBiz>(default, "LastRegionOf", game.Game);
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetLastRegionOfGame(GameBiz game, GameBiz value)
    {
        SetValue(value, "LastRegionOf", game.Game);
    }


    [Obsolete("Obsolete Setting", true)]
    public static string? GetGameBackgroundIds(GameId gameId)
    {
        return GetValue<string>(default, "GameBackgroundIds", gameId.ToString());
    }

    [Obsolete("Obsolete Setting", true)]
    public static void SetGameBackgroundIds(GameId gameId, string? value)
    {
        SetValue(value, "GameBackgroundIds", gameId.ToString());
    }


    /// <summary>
    /// 实时便笺提醒的检查间隔（分钟）。预计更早到点的项目会提前检查，与此无关。
    /// </summary>
    public static int DailyNoteNotifyIntervalMinutes
    {
        get => GetValue(60);
        set => SetValue(value);
    }


    /// <summary>
    /// 有游戏在运行时，便笺提醒不弹出横幅，直接进通知中心。默认开启。
    /// </summary>
    public static bool DailyNoteNotifySilentWhenGameRunning
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    #endregion



    #region 每日签到



    /// <summary>
    /// 该游戏是否每日自动签到（启动后静默签一轮，常驻期间跨日再签）。默认关闭，要用户在签到浮窗里打开。
    /// <para>国服与 B 服共用同一个键，读写前会先把 bilibili 区服归一化到国服。</para>
    /// </summary>
    public static bool GetAutoSignInEnabled(GameBiz gameBiz)
    {
        return GetValue(false, $"auto_sign_in_enabled_{gameBiz}");
    }


    public static void SetAutoSignInEnabled(GameBiz gameBiz, bool value)
    {
        SetValue(value, $"auto_sign_in_enabled_{gameBiz}");
    }



    #endregion



    #region Setting Method


    private static ConcurrentDictionary<string, string?> _settingCache = new();


    public static T? GetValue<T>(T? defaultValue = default, [CallerMemberName] string? key = null)
    {
        return GetValue<T>(defaultValue, key, "");
    }


    public static T? GetValue<T>(T? defaultValue, string? key, string gameId)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return defaultValue;
        }
        try
        {
            string cacheKey = $"{key}{gameId}";
            if (_settingCache.TryGetValue(cacheKey, out string? value))
            {
                return ConvertFromString(value, defaultValue);
            }
            using var dapper = DatabaseService.CreateConnection();
            value = dapper.QueryFirstOrDefault<string>("SELECT Value FROM Setting WHERE Key=@key AND GameId=@gameId LIMIT 1;", new { key, gameId });
            _settingCache[cacheKey] = value;
            return ConvertFromString(value, defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }


    private static T? ConvertFromString<T>(string? value, T? defaultValue = default)
    {
        if (value is null)
        {
            return defaultValue;
        }
        var converter = TypeDescriptor.GetConverter(typeof(T));
        if (converter == null)
        {
            return defaultValue;
        }
        return (T?)converter.ConvertFromString(value);
    }


    public static void SetValue<T>(T? value, [CallerMemberName] string? key = null)
    {
        SetValue<T>(value, key, "");
    }


    public static void SetValue<T>(T? value, string? key, string gameId)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return;
        }
        try
        {
            string cacheKey = $"{key}{gameId}";
            string? val = value?.ToString();
            if (_settingCache.TryGetValue(cacheKey, out string? cacheValue) && cacheValue == val)
            {
                return;
            }
            _settingCache[cacheKey] = val;
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("INSERT OR REPLACE INTO Setting (Key, GameId, Value) VALUES (@key, @gameId, @val);", new { key, gameId, val });
        }
        catch { }
    }


    public static void DeleteAllSettings()
    {
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("DELETE FROM Setting WHERE TRUE;");
        }
        catch { }
    }


    public static void ClearCache()
    {
        _settingCache.Clear();
    }


    #endregion




    #region Cloud Game & Favor Wallpaper (adapted from the Moonward main branch)


    // 好感壁纸与满影画壁纸的开关。主分支的键名与旧版本保持一致，用户已有的选择不会丢。
    // 0.19 的 AppConfig 还没有这些键（原提交依赖未迁移的 wiring 提交），这里先行补上。


    /// <summary>好感壁纸面板是否显示「满影画」而不是「好感」那一档。</summary>
    public static bool GetFavorWallpaperMindscapeMode(GameBiz biz)
    {
        return GetValue<bool>(default, $"favor_wallpaper_mindscape_{biz}");
    }

    public static void SetFavorWallpaperMindscapeMode(GameBiz biz, bool value)
    {
        SetValue(value, $"favor_wallpaper_mindscape_{biz}");
    }


    /// <summary>好感壁纸是否在切换游戏时随机换一张。</summary>
    public static bool GetFavorWallpaperShuffle(GameBiz biz)
    {
        return GetValue<bool>(default, $"favor_wallpaper_shuffle_{biz}");
    }

    public static void SetFavorWallpaperShuffle(GameBiz biz, bool value)
    {
        SetValue(value, $"favor_wallpaper_shuffle_{biz}");
    }


    /// <summary>满影画壁纸是否在切换游戏时随机换一张（与好感壁纸分开记，两者不混池）。</summary>
    public static bool GetMindscapeWallpaperShuffle(GameBiz biz)
    {
        return GetValue<bool>(default, $"mindscape_wallpaper_shuffle_{biz}");
    }

    public static void SetMindscapeWallpaperShuffle(GameBiz biz, bool value)
    {
        SetValue(value, $"mindscape_wallpaper_shuffle_{biz}");
    }



    /// <summary>该游戏是否自动领取云游戏每日免费时长。默认关闭，要用户在云游戏浮窗里主动打开。</summary>
    public static bool GetAutoCloudGameFreeTimeEnabled(GameBiz gameBiz)
    {
        return GetValue<bool>(default, $"auto_cloud_game_free_time_enabled_{gameBiz}");
    }

    public static void SetAutoCloudGameFreeTimeEnabled(GameBiz gameBiz, bool value)
    {
        SetValue(value, $"auto_cloud_game_free_time_enabled_{gameBiz}");
    }


    /// <summary>某个云游戏账号最近一次领取免费时长的服务端日期（按 UTC+8 的 4:00 分界）。</summary>
    public static DateOnly? GetCloudGameFreeTimeClaimedDate(GameBiz gameBiz, string accountId)
    {
        string? text = GetValue<string?>(null, $"cloud_game_free_time_claimed_date_{gameBiz}_{accountId}");
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", out DateOnly date) ? date : null;
    }

    public static void SetCloudGameFreeTimeClaimedDate(GameBiz gameBiz, string accountId, DateOnly date)
    {
        SetValue(date.ToString("yyyy-MM-dd"), $"cloud_game_free_time_claimed_date_{gameBiz}_{accountId}");
    }


    /// <summary>云游戏浮窗里当前选中的账号（<c>open_id</c>）。</summary>
    public static string? GetCloudGameSelectedAccount(GameBiz gameBiz)
    {
        return GetValue<string?>(null, $"cloud_game_selected_account_{gameBiz}");
    }

    public static void SetCloudGameSelectedAccount(GameBiz gameBiz, string? accountId)
    {
        SetValue(accountId, $"cloud_game_selected_account_{gameBiz}");
    }


    /// <summary>
    /// 云游戏账号的 <c>combo_token</c>，只从本机云游戏客户端读取并存在本地。
    /// <para>等同于账号的登录凭据，任何日志、诊断、上传路径都不要带上它。</para>
    /// </summary>
    public static string? GetCloudGameComboToken(GameBiz gameBiz, string accountId)
    {
        return GetValue<string?>(null, $"cloud_game_combo_token_{gameBiz}_{accountId}");
    }

    public static void SetCloudGameComboToken(GameBiz gameBiz, string accountId, string? value)
    {
        SetValue(value, $"cloud_game_combo_token_{gameBiz}_{accountId}");
    }


    /// <summary>列出该游戏已经保存过凭据的云游戏账号，用于在客户端日志读不到时仍能显示历史账号。</summary>
    public static List<string> GetCloudGameComboTokenAccountIds(GameBiz gameBiz)
    {
        List<string> list = new();
        string prefix = $"cloud_game_combo_token_{gameBiz}_";
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            foreach (string key in dapper.Query<string>("SELECT Key FROM Setting WHERE Key LIKE @pattern;", new { pattern = prefix + "%" }))
            {
                string accountId = key[prefix.Length..];
                if (!string.IsNullOrWhiteSpace(accountId) && !list.Contains(accountId))
                {
                    list.Add(accountId);
                }
            }
        }
        catch { }
        return list;
    }


    #endregion




    #region Passport Account (adapted from the Moonward main branch)

    // 通行证账号池（账号中心）的设置键。键名与主分支（main）保持一致，
    // 用户从 main 升级上来时数据库里已有的值可以直接复用；0.19 的 Setting 表在 v22 迁移时
    // 会把 main 的大写开头键原样搬到 GameId = '' 的行里。


    /// <summary>
    /// 当前通行证会话指向哪个账号，格式是「区域:账号 ID」（例如 <c>miHoYo:81107513</c>）。
    /// <para>
    /// 只存指针，不存令牌：SToken 在 <c>PassportUserInfo</c> 表里（唯一一份），
    /// 派生令牌和上游一样只存在工具箱 Cookie 里，重启时按这个指针去上游表读回。
    /// </para>
    /// </summary>
    public static string? CurrentPassportAccount
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>账号列表拖动排序用的设置键（按区域各存一份，值是逗号分隔的账号 ID）。</summary>
    private const string PassportUserOrderKey = "PassportUserOrder";


    /// <summary>
    /// 账号中心里账号列表的排列顺序。账号中心（上游 HYP 浮窗）与账号池共用这一份，
    /// 拖动排序后保存，重启后仍然有效。
    /// </summary>
    public static string? GetPassportUserOrder(ServerRegion region)
    {
        return GetValue<string>(null, PassportUserOrderKey, region.ToString());
    }


    public static void SetPassportUserOrder(ServerRegion region, string? order)
    {
        SetValue(order, PassportUserOrderKey, region.ToString());
    }



    #endregion



}
