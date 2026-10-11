using Dapper;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.Database;
using Starward.Features.GameLauncher;
using Starward.Features.ViewHost;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Starward;

public static partial class AppConfig
{


    #region Static Setting


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

    public static bool EnableBannerAndPost
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static bool IgnoreRunningGame
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ShowNoviceGacha
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ShowChronicledWish
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

    [Obsolete("已不用", true)]
    public static bool UseOneBg
    {
        get => GetValue<bool>();
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

    public static bool EnableSystemTrayIcon
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ExitWhenClosing
    {
        get => GetValue<bool>();
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

    public static bool UseSystemThemeColor
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool EnableNavigationViewLeftCompact
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 基于注册表的游戏账号切换：开启后启动页显示游戏账号切换器，
    /// 切账号时读写游戏客户端自己记住的那个账号（注册表里那份，每个游戏各记一份）。
    /// </summary>
    public static bool EnableRegistryGameAccountSwitcher
    {
        get => GetValue(GetValue(false, ObsoleteGameAccountSwitcherKey), nameof(EnableRegistryGameAccountSwitcher));
        set => SetValue(value, nameof(EnableRegistryGameAccountSwitcher));
    }


    /// <summary>改名前的设置键，只当作默认值读一次，免得升级后把用户已经打开的开关关掉。</summary>
    public const string ObsoleteGameAccountSwitcherKey = "EnableGameAccountSwitcher";

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

    public static string? HyperionDeviceId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? HyperionDeviceFp
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static DateTimeOffset HyperionDeviceFpLastUpdateTime
    {
        get => GetValue<DateTimeOffset>();
        set => SetValue(value);
    }

    /// <summary>
    /// 设备名。UA、<c>x-rpc-device_name</c> 与 getFp 的 body 三处共用，默认取真机抓包里的值，
    /// 免得 UA 自称一台机器、body 里的 ext_fields 又说另一台。
    /// </summary>
    public static string HyperionDeviceName
    {
        get => GetValue(HyperionClient.DefaultDeviceName)!;
        set => SetValue(value);
    }

    /// <summary>Android 版本，同时用作 getFp body 里的 osVersion 与 sdkVersion。</summary>
    public static string HyperionSystemVersion
    {
        get => GetValue(HyperionClient.DefaultSystemVersion)!;
        set => SetValue(value);
    }

    /// <summary>米游社 App 版本，同时用于 UA 与 <c>x-rpc-app_version</c> 请求头。</summary>
    public static string HyperionAppVersion
    {
        get => GetValue(HyperionClient.DefaultAppVersion)!;
        set => SetValue(value);
    }

    /// <summary>
    /// 通行证令牌（JSON）。保存在数据库中，不再写入 config.ini 或注册表。
    /// </summary>
    public static string? PassportTokenJson
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 账号中心里账号列表的排列顺序（账号标识，逗号分隔）。拖动排序后保存，重启后仍然有效。
    /// </summary>
    public static string? PassportAccountOrder
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 游戏授权登录（自动登录）。默认开启，不再依赖 ini 或注册表中的旧开关。
    /// <para>
    /// 旧开关只要不存在就会被读成关闭，并把本应默认开启的功能关掉（数据库里留下 False），
    /// 因此这里换用新的键名，旧值不再读取，等于一次性恢复默认值；关闭需要用户在界面上主动操作。
    /// </para>
    /// </summary>
    public static bool EnableAuthTicketLogin
    {
        get => GetValue(true, EnableAuthTicketLoginKey);
        set => SetValue(value, EnableAuthTicketLoginKey);
    }


    /// <summary>存储 <see cref="EnableAuthTicketLogin"/> 的设置键，见该属性的说明。</summary>
    public const string EnableAuthTicketLoginKey = "EnableAuthTicketLoginV2";


    /// <summary>
    /// 某个游戏是否在启动时自动登录。开关放在该游戏的启动参数设置里，所以按游戏分别保存。
    /// <para>
    /// 没有单独设置过的游戏沿用 <see cref="EnableAuthTicketLogin"/> 的值，
    /// 这样旧版本里关掉自动登录的用户升级后不会被重新打开。
    /// </para>
    /// </summary>
    public static bool GetEnableAuthTicketLogin(GameBiz gameBiz)
    {
        string? key = GetEnableAuthTicketLoginKeyName(gameBiz);
        if (key is null)
        {
            return EnableAuthTicketLogin;
        }
        return GetValue(EnableAuthTicketLogin, key);
    }


    public static void SetEnableAuthTicketLogin(GameBiz gameBiz, bool value)
    {
        string? key = GetEnableAuthTicketLoginKeyName(gameBiz);
        if (key is null)
        {
            EnableAuthTicketLogin = value;
        }
        else
        {
            SetValue(value, key);
        }
    }


    private static string? GetEnableAuthTicketLoginKeyName(GameBiz gameBiz)
    {
        return string.IsNullOrWhiteSpace(gameBiz.Value) ? null : $"EnableAuthTicketLogin_{gameBiz.Value}";
    }


    /// <summary>旧版本使用过的设置键，其中的值已不再读取，仅在启动时清理。</summary>
    public const string ObsoleteEnableAuthTicketLoginKey = "EnableAuthTicketLogin";


    /// <summary>
    /// 某个游戏当前选用的通行证账号（账号标识，见 <c>PassportAccountRecord.GetKey</c>）。
    /// <para>
    /// 一个通行证账号在一个游戏里只对应一个游戏账号，所以每个游戏各记一份就够了。
    /// </para>
    /// </summary>
    public static string? GetCurrentPassportAccountKey(GameBiz gameBiz)
    {
        return GetValue<string>(null, GetCurrentPassportAccountKeyName(gameBiz));
    }


    public static void SetCurrentPassportAccountKey(GameBiz gameBiz, string? key)
    {
        SetValue(key, GetCurrentPassportAccountKeyName(gameBiz));
    }


    private static string? GetCurrentPassportAccountKeyName(GameBiz gameBiz)
    {
        return string.IsNullOrWhiteSpace(gameBiz.Value) ? null : $"PassportAccountKey_{gameBiz.Value}";
    }

    public static string? LastAppVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 当前选择的游戏区服
    /// </summary>
    public static GameBiz CurrentGameBiz
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? SelectedGameBizs
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 固定待选择的游戏区服图标
    /// </summary>
    public static bool IsGameBizSelectorPinned
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static string? DefaultGameInstallationPath
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static int SpeedLimitKBPerSecond
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 缓存的游戏信息 <see cref="Starward.Core.HoYoPlay.GameInfo"/>
    /// </summary>
    public static string? CachedGameInfo
    {
        get => DatabaseService.GetValue<string>(nameof(CachedGameInfo), out _, default);
        set => DatabaseService.SetValue(nameof(CachedGameInfo), value);
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
    /// 保持 RPC 服务在后台运行
    /// </summary>
    public static bool KeepRpcServerRunningInBackground
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 安装游戏时自动创建子文件夹
    /// </summary>
    public static bool AutomaticallyCreateSubfolderForInstall
    {
        get => GetValue(true);
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


    #endregion



    #region Dynamic Setting


    public static string? GetBg(GameBiz biz)
    {
        return GetValue<string>(default, $"bg_{biz}");
    }

    public static void SetBg(GameBiz biz, string? value)
    {
        SetValue(value, $"bg_{biz}");
    }


    public static bool GetUseVersionPoster(GameBiz biz)
    {
        return GetValue<bool>(default, $"use_version_poster_{biz}");
    }

    public static void SetUseVersionPoster(GameBiz biz, bool value)
    {
        SetValue(value, $"use_version_poster_{biz}");
    }


    public static string? GetVersionPoster(GameBiz biz)
    {
        return GetValue<string>(default, $"version_poster_{biz}");
    }

    public static void SetVersionPoster(GameBiz biz, string? value)
    {
        SetValue(value, $"version_poster_{biz}");
    }


    public static string? GetCustomBg(GameBiz biz)
    {
        return GetValue<string>(default, $"custom_bg_{biz}");
    }

    public static void SetCustomBg(GameBiz biz, string? value)
    {
        SetValue(value, $"custom_bg_{biz}");
    }


    public static bool GetEnableCustomBg(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_custom_bg_{biz}");
    }

    public static void SetEnableCustomBg(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_custom_bg_{biz}");
    }


    public static string? GetGameInstallPath(GameBiz biz)
    {
        return GetValue<string>(default, $"install_path_{biz}");
    }

    public static void SetGameInstallPath(GameBiz biz, string? value)
    {
        SetValue(value, $"install_path_{biz}");
    }


    public static bool GetGameInstallPathRemovable(GameBiz biz)
    {
        return GetValue<bool>(default, $"install_path_removable_{biz}");
    }

    public static void SetGameInstallPathRemovable(GameBiz biz, bool value)
    {
        SetValue(value, $"install_path_removable_{biz}");
    }


    public static bool GetEnableThirdPartyTool(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_third_party_tool_{biz}");
    }

    public static void SetEnableThirdPartyTool(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_third_party_tool_{biz}");
    }


    public static string? GetThirdPartyToolPath(GameBiz biz)
    {
        return GetValue<string>(default, $"third_party_tool_path_{biz}");
    }

    public static void SetThirdPartyToolPath(GameBiz biz, string? value)
    {
        SetValue(value, $"third_party_tool_path_{biz}");
    }


    public static string? GetStartArgument(GameBiz biz)
    {
        return GetValue<string>(default, $"start_argument_{biz}");
    }

    public static void SetStartArgument(GameBiz biz, string? value)
    {
        SetValue(value, $"start_argument_{biz}");
    }


    /// <summary>
    /// 无边框窗口
    /// </summary>
    /// <param name="biz"></param>
    /// <returns></returns>
    public static bool GetUsePopupWindow(GameBiz biz)
    {
        return GetValue<bool>(false, $"use_popup_window_{biz}");
    }

    /// <summary>
    /// 无边框窗口
    /// </summary>
    /// <param name="biz"></param>
    /// <param name="value"></param>
    public static void SetUsePopupWindow(GameBiz biz, bool value)
    {
        SetValue(value, $"use_popup_window_{biz}");
    }


    public static long GetLastUidInGachaLogPage(GameBiz biz)
    {
        return GetValue<long>(default, $"last_gacha_uid_{biz}");
    }

    public static void SetLastUidInGachaLogPage(GameBiz biz, long value)
    {
        SetValue(value, $"last_gacha_uid_{biz}");
    }


    [Obsolete("已不用")]
    public static GameBiz GetLastRegionOfGame(GameBiz game)
    {
        return GetValue<GameBiz>(default, $"last_region_of_{game}");
    }

    [Obsolete("已不用")]
    public static void SetLastRegionOfGame(GameBiz game, GameBiz value)
    {
        SetValue(value, $"last_region_of_{game}");
    }


    public static string? GetDisplayGachaBanners(GameBiz biz)
    {
        return GetValue<string>(default, $"display_gacha_banners_{biz}");
    }

    public static void SetDisplayGachaBanners(GameBiz biz, string value)
    {
        SetValue(value, $"display_gacha_banners_{biz}");
    }


    /// <summary>
    /// 外部截图文件夹
    /// </summary>
    /// <param name="biz"></param>
    /// <returns></returns>
    public static string? GetExternalScreenshotFolder(GameBiz biz)
    {
        return GetValue<string>(default, $"external_screenshot_folder_{biz}");
    }

    /// <summary>
    /// 外部截图文件夹
    /// </summary>
    /// <param name="biz"></param>
    /// <param name="value"></param>
    public static void SetExternalScreenshotFolder(GameBiz biz, string? value)
    {
        SetValue(value, $"external_screenshot_folder_{biz}");
    }


    public static string? GetGameBackgroundIds(GameBiz biz)
    {
        return GetValue<string>(default, $"game_background_ids_{biz}");
    }

    public static void SetGameBackgroundIds(GameBiz biz, string? value)
    {
        SetValue(value, $"game_background_ids_{biz}");
    }


    /// <summary>
    /// 启用 DX12
    /// </summary>
    public static bool GetEnableDX12(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_dx12_{biz}");
    }

    /// <summary>
    /// 启用 DX12
    /// </summary>
    public static void SetEnableDX12(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_dx12_{biz}");
    }


    #endregion



    #region Setting Method


    private static Dictionary<string, string?> _settingCache;


    private static void InitializeSettingProvider()
    {
        try
        {
            if (_settingCache is null)
            {
                using var dapper = DatabaseService.CreateConnection();
                _settingCache = dapper.Query<(string Key, string? Value)>("SELECT Key, Value FROM Setting;").ToDictionary(x => x.Key, x => x.Value);
            }
        }
        catch { }
    }


    public static T? GetValue<T>(T? defaultValue = default, [CallerMemberName] string? key = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return defaultValue;
        }
        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return defaultValue;
        }
        try
        {
            if (_settingCache.TryGetValue(key, out string? value))
            {
                return ConvertFromString(value, defaultValue);
            }
            using var dapper = DatabaseService.CreateConnection();
            value = dapper.QueryFirstOrDefault<string>("SELECT Value FROM Setting WHERE Key=@key LIMIT 1;", new { key });
            _settingCache[key] = value;
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
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return;
        }
        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return;
        }
        try
        {
            string? val = value?.ToString();
            if (_settingCache.TryGetValue(key, out string? cacheValue) && cacheValue == val)
            {
                return;
            }
            _settingCache[key] = val;
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @val);", new { key, val });
        }
        catch { }
    }


    /// <summary>
    /// 删除一个设置项（同时清掉缓存）。用于清理被旧版本写坏或已经废弃的键。
    /// </summary>
    public static void RemoveValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return;
        }
        try
        {
            InitializeSettingProvider();
            if (_settingCache is not null)
            {
                _settingCache.Remove(key);
            }
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("DELETE FROM Setting WHERE Key=@key;", new { key });
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


}
