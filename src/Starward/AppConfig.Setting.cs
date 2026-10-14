using Dapper;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.Database;
using Starward.Features.GameLauncher;
using Starward.Features.ViewHost;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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

    /// <summary>
    /// 是否显示游戏公告。
    /// <para>
    /// 现在属于启动预设，见 <see cref="GetEnableBannerAndPost"/>；这个全局值只作为旧数据的回退，
    /// 也是预设里还没记过这一项时的默认值（默认开启）。
    /// </para>
    /// </summary>
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
    /// 设备名。UA、<c>x-rpc-device_name</c> 与 getFp 的 body 三处共用。
    /// 配置里有值就用配置里的，没有才回落到 <see cref="HyperionClient.DefaultDeviceName"/>（仓库自带的公开机型），
    /// 免得 UA 自称一台机器、body 里的 ext_fields 又说另一台。
    /// <para>
    /// 读取时统一走 <see cref="HyperionClient.NormalizeDeviceName(string?)"/>：配置里可能存着从抓包
    /// 整段粘进来的转义形式（例如 <c>Pixel%205</c>），直接使用会被 UA 与请求头再转义一次。
    /// 配置文件里的原值不改写，只归一化读出来的值。
    /// </para>
    /// </summary>
    public static string HyperionDeviceName
    {
        get => HyperionClient.NormalizeDeviceName(GetValue<string>()) is { Length: > 0 } name ? name : HyperionClient.DefaultDeviceName;
        set => SetValue(value);
    }

    /// <summary>Android 版本，同时用作 getFp body 里的 osVersion 与 sdkVersion。配置里有值就用配置里的，没有才回落到默认值。</summary>
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
    /// getFp 请求体里的 <c>device_id</c>。真机上是 16 位十六进制的 ANDROID_ID，
    /// 与 <see cref="HyperionDeviceId"/>（米游社自己的设备号）不是同一个值，但同样要跨刷新稳定复用。
    /// </summary>
    public static string? HyperionDeviceAndroidId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// getFp 使用的 seed_id。必须与设备指纹成对稳定复用，否则每次重刷指纹都像是换了一台设备。
    /// </summary>
    public static string? HyperionDeviceFpSeedId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>getFp 使用的 seed_time（毫秒时间戳字符串），与 <see cref="HyperionDeviceFpSeedId"/> 成对复用。</summary>
    public static string? HyperionDeviceFpSeedTime
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 已保存的设备指纹是按哪个 <c>ext_fields</c> 载荷版本申请的。
    /// 旧载荷含 Windows 硬件字段，会被服务端当成模拟器，所以版本不一致时必须强制重刷一次。
    /// </summary>
    public static int HyperionDeviceFpPayloadVersion
    {
        get => GetValue<int>();
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
    /// <para>
    /// 现在每个区服可以有多个启动预设，这个开关属于预设。这里读写的是该区服的
    /// <b>第一个</b>预设（旧版本的单套配置迁移过来的那个），供
    /// <see cref="Features.GameSetting.GameSettingPage"/> 等没有预设概念的调用方使用；
    /// 启动器页面请用 <see cref="GameLaunchProfileStore"/>。
    /// </para>
    /// </summary>
    public static bool GetEnableAuthTicketLogin(GameBiz gameBiz)
    {
        string? key = GetEnableAuthTicketLoginKeyName(gameBiz);
        if (key is null)
        {
            return EnableAuthTicketLogin;
        }
        bool legacy = GetValue(EnableAuthTicketLogin, key);
        if (FindProfile(gameBiz, current: false) is GameLaunchProfile profile)
        {
            return profile.EnableAuthTicketLogin;
        }
        return legacy;
    }


    public static void SetEnableAuthTicketLogin(GameBiz gameBiz, bool value)
    {
        string? key = GetEnableAuthTicketLoginKeyName(gameBiz);
        if (key is null)
        {
            EnableAuthTicketLogin = value;
            return;
        }
        if (FindProfile(gameBiz, current: false) is GameLaunchProfile profile)
        {
            profile.EnableAuthTicketLogin = value;
            GameLaunchProfileStore.Save(gameBiz);
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


    #endregion



    #region Dynamic Setting


    /// <summary>
    /// 某个区服当前正在使用的背景文件名（启动器底部那排指示器选中的那张）。
    /// <para>
    /// 背景（壁纸类型）属于启动预设：内部走该区服<b>当前选中</b>预设的
    /// <see cref="GameLaunchProfile.Bg"/>，这样切换预设时画面能跟着换成该预设保存的那张。
    /// 旧数据没有这个字段时（读出 null）回退到旧键 <c>bg_{biz}</c>，用户当前背景不会丢。
    /// </para>
    /// </summary>
    public static string? GetBg(GameBiz biz)
    {
        string? legacy = GetValue<string>(default, $"bg_{biz}");
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            return profile.Bg ?? legacy;
        }
        return legacy;
    }

    public static void SetBg(GameBiz biz, string? value)
    {
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            profile.Bg = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"bg_{biz}");
        }
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


    /// <summary>
    /// 某个区服当前选中的启动预设的自定义背景文件名。
    /// <para>
    /// 自定义背景属于启动预设，背景服务和壁纸等没有预设概念的调用方读到的是
    /// 该区服<b>当前选中</b>预设的值，这样界面上的预设和实际显示的背景才一致。
    /// </para>
    /// </summary>
    public static string? GetCustomBg(GameBiz biz)
    {
        string legacyKey = $"custom_bg_{biz}";
        string? legacy = GetValue<string>(default, legacyKey);
        // 预设数据写不进去时（例如数据文件夹还没就绪）退回旧键，行为与旧版本一致。
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            return profile.CustomBg;
        }
        return legacy;
    }

    public static void SetCustomBg(GameBiz biz, string? value)
    {
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            profile.CustomBg = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"custom_bg_{biz}");
        }
    }


    /// <summary>
    /// 某个区服当前选中的启动预设是否启用自定义背景。
    /// </summary>
    public static bool GetEnableCustomBg(GameBiz biz)
    {
        bool legacy = GetValue<bool>(default, $"enable_custom_bg_{biz}");
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            return profile.EnableCustomBg;
        }
        return legacy;
    }

    public static void SetEnableCustomBg(GameBiz biz, bool value)
    {
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            profile.EnableCustomBg = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"enable_custom_bg_{biz}");
        }
    }


    /// <summary>
    /// 某个区服当前选中的启动预设是否显示游戏公告。
    /// <para>
    /// 显示游戏公告属于启动预设。预设里还没有这一项（旧数据、或新建后没改过）时回退到全局键
    /// <see cref="EnableBannerAndPost"/>，默认开启，所以老用户升级后公告不会莫名消失。
    /// 写入端是设置对话框，它直接改当前预设，见 <see cref="GameLaunchProfile.EnableBannerAndPost"/>。
    /// </para>
    /// </summary>
    public static bool GetEnableBannerAndPost(GameBiz biz)
    {
        if (FindProfile(biz, current: true) is GameLaunchProfile profile && profile.EnableBannerAndPost is bool value)
        {
            return value;
        }
        return EnableBannerAndPost;
    }


    /// <summary>
    /// 在预设列表里取一个预设。<paramref name="current"/> 为 true 时取当前选中的那个，
    /// 否则取第一个（旧配置迁移过来的那个）。预设列表还是空的时候返回 null。
    /// </summary>
    private static GameLaunchProfile? FindProfile(GameBiz biz, bool current)
    {
        List<GameLaunchProfile> profiles = GameLaunchProfileStore.GetProfiles(biz);
        if (profiles.Count == 0)
        {
            return null;
        }
        if (current && GetCurrentLaunchProfileId(biz) is string id)
        {
            return profiles.FirstOrDefault(x => x.Id == id) ?? profiles[0];
        }
        return profiles[0];
    }


    /// <summary>
    /// 好感壁纸面板是否显示「满影画」而不是「好感」那一档。键名与旧版本保持一致，用户已有的选择不会丢。
    /// </summary>
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


    /// <summary>
    /// 某个区服当前选中的启动预设是否启用自定义启动程序。
    /// <para>
    /// 自定义启动程序属于启动预设，这里读写的是该区服<b>当前选中</b>预设的值
    /// （启动游戏时用的就是当前预设），供没有预设概念的调用方使用；
    /// 旧数据没有这个字段时回退到旧键 <c>enable_third_party_tool_{biz}</c>。
    /// </para>
    /// </summary>
    public static bool GetEnableThirdPartyTool(GameBiz biz)
    {
        bool legacy = GetValue<bool>(default, $"enable_third_party_tool_{biz}");
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            return profile.EnableThirdPartyTool;
        }
        return legacy;
    }

    public static void SetEnableThirdPartyTool(GameBiz biz, bool value)
    {
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            profile.EnableThirdPartyTool = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"enable_third_party_tool_{biz}");
        }
    }


    /// <summary>
    /// 某个区服当前选中的启动预设里自定义启动程序的路径，见 <see cref="GetEnableThirdPartyTool"/>。
    /// </summary>
    public static string? GetThirdPartyToolPath(GameBiz biz)
    {
        string? legacy = GetValue<string>(default, $"third_party_tool_path_{biz}");
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            return profile.ThirdPartyToolPath;
        }
        return legacy;
    }

    public static void SetThirdPartyToolPath(GameBiz biz, string? value)
    {
        if (FindProfile(biz, current: true) is GameLaunchProfile profile)
        {
            profile.ThirdPartyToolPath = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"third_party_tool_path_{biz}");
        }
    }


    /// <summary>
    /// 某个区服的启动参数。
    /// <para>
    /// 启动参数属于启动预设，这里读写的是该区服的<b>第一个</b>预设
    /// （旧版本的单套配置迁移过来的那个），供没有预设概念的调用方使用；
    /// 启动器页面请用 <see cref="GameLaunchProfileStore"/>。
    /// </para>
    /// </summary>
    public static string? GetStartArgument(GameBiz biz)
    {
        string? legacy = GetValue<string>(default, $"start_argument_{biz}");
        if (FindProfile(biz, current: false) is GameLaunchProfile profile)
        {
            return profile.Argument;
        }
        return legacy;
    }

    public static void SetStartArgument(GameBiz biz, string? value)
    {
        if (FindProfile(biz, current: false) is GameLaunchProfile profile)
        {
            profile.Argument = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"start_argument_{biz}");
        }
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
    /// 是否启用 DX12。
    /// <para>
    /// 属于启动预设，这里读写的是该区服的<b>第一个</b>预设
    /// （旧版本的单套配置迁移过来的那个），供没有预设概念的调用方使用；
    /// 启动器页面请用 <see cref="GameLaunchProfileStore"/>。
    /// </para>
    /// </summary>
    public static bool GetEnableDX12(GameBiz biz)
    {
        bool legacy = GetValue<bool>(default, $"enable_dx12_{biz}");
        if (FindProfile(biz, current: false) is GameLaunchProfile profile)
        {
            return profile.EnableDX12;
        }
        return legacy;
    }

    /// <summary>
    /// 是否启用 DX12，见 <see cref="GetEnableDX12"/>。
    /// </summary>
    public static void SetEnableDX12(GameBiz biz, bool value)
    {
        if (FindProfile(biz, current: false) is GameLaunchProfile profile)
        {
            profile.EnableDX12 = value;
            GameLaunchProfileStore.Save(biz);
        }
        else
        {
            SetValue(value, $"enable_dx12_{biz}");
        }
    }


    /// <summary>
    /// 某个区服当前选中的启动预设（内部名）。没有记录时返回 null，此时按第一个预设处理。
    /// </summary>
    public static string? GetCurrentLaunchProfileId(GameBiz biz)
    {
        return GetValue<string>(default, $"current_launch_profile_{biz}");
    }


    public static void SetCurrentLaunchProfileId(GameBiz biz, string? id)
    {
        SetValue(id, $"current_launch_profile_{biz}");
    }


    #endregion



    #region 云游戏



    /// <summary>该游戏是否自动领取云游戏每日免费时长。默认关闭，要用户在云游戏浮窗里主动打开。</summary>
    public static bool GetAutoCloudGameFreeTimeEnabled(GameBiz gameBiz)
    {
        return GetValue(false, $"auto_cloud_game_free_time_enabled_{gameBiz}");
    }


    public static void SetAutoCloudGameFreeTimeEnabled(GameBiz gameBiz, bool value)
    {
        SetValue(value, $"auto_cloud_game_free_time_enabled_{gameBiz}");
    }


    /// <summary>某个云游戏账号最近一次领取免费时长的服务端日期（按 UTC+8 的 4:00 分界）。</summary>
    public static DateOnly? GetCloudGameFreeTimeClaimedDate(GameBiz gameBiz, string accountId)
    {
        string? text = GetValue<string?>(null, $"cloud_game_free_time_claimed_date_{gameBiz}_{accountId}");
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
    }


    public static void SetCloudGameFreeTimeClaimedDate(GameBiz gameBiz, string accountId, DateOnly date)
    {
        SetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), $"cloud_game_free_time_claimed_date_{gameBiz}_{accountId}");
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
            InitializeSettingProvider();
            if (_settingCache is null)
            {
                return list;
            }
            foreach (string key in _settingCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string accountId = key[prefix.Length..];
                    if (!string.IsNullOrWhiteSpace(accountId) && !list.Contains(accountId))
                    {
                        list.Add(accountId);
                    }
                }
            }
        }
        catch { }
        return list;
    }


    /// <summary>删除某个通行证账号关联的全部云游戏凭据与领取记录，用于账号被移除时一并清理。</summary>
    public static void DeleteCloudGameAccountData(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }
        try
        {
            InitializeSettingProvider();
            if (_settingCache is null)
            {
                return;
            }
            string suffix = $"_{accountId}";
            foreach (string key in _settingCache.Keys.ToList())
            {
                if (key.EndsWith(suffix, StringComparison.Ordinal)
                    && (key.StartsWith("cloud_game_combo_token_", StringComparison.Ordinal)
                        || key.StartsWith("cloud_game_free_time_claimed_date_", StringComparison.Ordinal)))
                {
                    RemoveValue(key);
                }
            }
        }
        catch { }
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
