using Dapper;
using Microsoft.Data.Sqlite;
using Starward.Core.DeviceFp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace Starward.Features.Database;

/// <summary>
/// 从 main（0.18）升级上来之后的一次性旧键清理。
/// <para>
/// v24 已经把旧数据搬进新结构（当前游戏、启动预设、账号池、安装路径、工具箱角色）。这些数据在新结构里
/// 都有自己的唯一 owner；旧键如果继续留着，就变成同一份数据的第二个副本：改新结构不会改旧键，
/// 而旧程序（0.18 安装版）再运行一次还可能把旧键改回去。所以迁移完成后把这些旧键删掉。
/// </para>
/// <para>
/// 可以在已有数据的库上重复执行：只做删除，不写任何新数据；没有旧键时什么都不做。
/// 只处理本分支升级上来的库（靠 <c>LegacyMainDatabaseMigrated</c> 标记识别），
/// 全新安装和上游 0.19 用户的库不会被误删。
/// </para>
/// </summary>
internal static class LegacySettingCleanup
{

    /// <summary>清理完成标记，避免每次启动都扫一遍。</summary>
    private const string DoneKey = "LegacySettingCleanupDone";


    /// <summary>分支升级标记，只有从 main 升级上来的库才有。</summary>
    private const string ForkDatabaseKey = "LegacyMainDatabaseMigrated";


    /// <summary>
    /// 整类删掉的全局旧键（<c>GLOB</c> 区分大小写，避免误伤上游新键，例如 <c>bg_*</c> 不会碰到 <c>Bg</c>）。
    /// </summary>
    private static readonly string[] LegacyKeyGlobs =
    [
        // 自定义背景开关：已经并入启动预设（GameLaunchProfile.EnableCustomBg）
        "enable_custom_bg_*",
        // main 按区服记的米游社账号：账号中心统一控制登录账号后不再使用
        "PassportAccountKey_*",
        // 已重命名为上游 LastSelectGameRecordRole（GameId = GameBiz）
        "last_select_game_record_role_*",
        // 已重命名为上游 LastRegionOf（GameId = 游戏）
        "last_region_of_*",
        // 已重命名为上游 GameBackground
        "game_background_ids_*",
        // 0.19 已删除的功能，对应的旧设置键
        "use_popup_window_*",
        "version_poster_*",
        // 旧背景图键：已重命名为上游 Bg（GameId = GameBiz-GameId）
        "bg_*",
        // 旧抽卡记录 UID / 同步角色：键名带游戏名后缀，v22 的重命名没匹配上，上游新键已经是唯一 owner
        "last_gacha_uid_*",
        "last_select_gacha_sync_role_*",
        // 旧安装路径键：v24 已搬进 GameEntry
        "install_path_*",
    ];


    /// <summary>整行删掉的全局旧键。</summary>
    private static readonly string[] LegacyKeys =
    [
        // 账号顺序 / 整包令牌：PassportAccountMigration 已经收口，旧键只作为迁移输入
        "PassportAccountOrder",
        "PassportTokenJson",
        // 当前游戏 / 自动登录：已经迁移到上游 CurrentGameId / AuthLoginGameByPassportAccount
        "CurrentGameBiz",
        "EnableAuthTicketLoginV2",
        // main 的游戏选择器遗留
        "SelectGameBiz",
        // 旧游戏列表缓存：0.19 使用 FusionCache（CacheEntry 表）
        "CachedGameInfo",
    ];


    /// <summary>
    /// 上游（0.19）按 <c>GameId</c> 存的单套启动配置。本分支把这些值收进启动预设，
    /// 只对已经有预设的区服删除：没有预设的区服还要用它们在第一次读取时播种。
    /// </summary>
    private static readonly string[] UpstreamGameKeys =
    [
        "StartArgument",
        "EnableThirdPartyTool",
        "ThirdPartyToolPath",
        "EnableDX12",
        "CustomBg",
        "Bg",
        "EnableCustomBg",
        "UsePopupWindow",
        "GameBackgroundIds",
    ];



    /// <summary>
    /// 执行清理。可以在已有数据的库上重复执行。
    /// </summary>
    public static void Migrate(SqliteConnection con)
    {
        try
        {
            if (!IsForkDatabase(con) || IsDone(con))
            {
                return;
            }
            DeleteGlobalLegacyKeys(con);
            DeleteTransitionInstallPathKeys(con);
            foreach (string gameBiz in GetGameBizWithProfiles(con))
            {
                DeleteUpstreamGameSettings(con, gameBiz);
                DeleteLegacyKey(con, $"enable_custom_bg_{gameBiz}");
            }
            AdoptLegacyDeviceInfo(con);
            // 删完全部旧键后再写标记：中途失败时下次启动会重试（删除本身是幂等的）。
            con.Execute("INSERT OR REPLACE INTO Setting (Key, GameId, Value) VALUES (@key, '', '1');", new { key = DoneKey });
            Debug.WriteLine("Cleaned up the legacy settings of the main branch.");
        }
        catch (Exception ex)
        {
            // 清理失败不能拦住启动；未清理完的键下次启动会再试。
            Debug.WriteLine($"Clean up the legacy settings failed: {ex}");
        }
    }



    private static bool IsForkDatabase(SqliteConnection con)
    {
        return con.QueryFirstOrDefault<long>("""
            SELECT COUNT(1) FROM Setting
            WHERE Key = @key AND GameId = '' AND Value = '1';
            """, new { key = ForkDatabaseKey }) > 0;
    }



    private static bool IsDone(SqliteConnection con)
    {
        return con.QueryFirstOrDefault<long>("""
            SELECT COUNT(1) FROM Setting
            WHERE Key = @key AND GameId = '';
            """, new { key = DoneKey }) > 0;
    }



    private static void DeleteGlobalLegacyKeys(SqliteConnection con)
    {
        con.Execute("DELETE FROM Setting WHERE GameId = '' AND Key IN @keys;", new { keys = LegacyKeys });
        foreach (string pattern in LegacyKeyGlobs)
        {
            con.Execute("DELETE FROM Setting WHERE GameId = '' AND Key GLOB @pattern;", new { pattern });
        }
    }



    /// <summary>
    /// v22 把安装路径重命名成 <c>InstallPath</c> / <c>InstallPathRemovable</c>，v24 再搬进 <c>GameEntry</c>。
    /// 搬迁只处理有值的行，空值 / 已卸载游戏的行会留下，这里把过渡键整表清掉。
    /// </summary>
    private static void DeleteTransitionInstallPathKeys(SqliteConnection con)
    {
        con.Execute("DELETE FROM Setting WHERE Key IN ('InstallPath', 'InstallPathRemovable');");
    }



    /// <summary>有启动预设（<c>launch_profiles_{GameBiz}</c>）的区服。</summary>
    private static List<string> GetGameBizWithProfiles(SqliteConnection con)
    {
        const string prefix = "launch_profiles_";
        var keys = con.Query<string>("""
            SELECT Key FROM Setting
            WHERE GameId = '' AND Key GLOB 'launch_profiles_*';
            """).AsList();
        var list = new List<string>(keys.Count);
        foreach (string key in keys)
        {
            string gameBiz = key[prefix.Length..];
            if (!string.IsNullOrWhiteSpace(gameBiz) && !list.Contains(gameBiz))
            {
                list.Add(gameBiz);
            }
        }
        return list;
    }



    /// <summary>删除某个区服按 <c>GameId</c> 存的旧启动配置（<c>GameId</c> 形如 <c>hk4e_cn-1Z8W5NHUQb</c>）。</summary>
    private static void DeleteUpstreamGameSettings(SqliteConnection con, string gameBiz)
    {
        con.Execute("""
            DELETE FROM Setting
            WHERE Key IN @keys AND GameId GLOB @pattern;
            """, new { keys = UpstreamGameKeys, pattern = $"{gameBiz}-*" });
    }



    /// <summary>
    /// 设备信息收口：旧 main 的 <c>HyperionDevice*</c> 里存着账号已经登记过的那枚设备指纹。
    /// 0.19 的 <c>AndroidDeviceInfo</c> 才是唯一 owner；升级后如果 0.19 已经先存过一份随机设备信息，
    /// 也要换回旧 main 登记过的那枚，否则服务端的「账号 - 设备」绑定会被打破。换完把旧键删掉。
    /// </summary>
    private static void AdoptLegacyDeviceInfo(SqliteConnection con)
    {
        string? json = con.ExecuteScalar<string?>("""
            SELECT Value FROM Setting WHERE GameId = '' AND Key = 'AndroidDeviceInfo' LIMIT 1;
            """);
        if (string.IsNullOrWhiteSpace(json))
        {
            // 0.19 还没有设备信息：留给运行时在创建时采用旧键（此时不能删旧键）。
            return;
        }
        try
        {
            AndroidDeviceInfo? info = JsonSerializer.Deserialize(json, DeviceFpJsonContext.Default.AndroidDeviceInfo);
            if (info is null)
            {
                return;
            }
            string? deviceFp = con.ExecuteScalar<string?>("""
                SELECT Value FROM Setting WHERE GameId = '' AND Key = 'HyperionDeviceFp' LIMIT 1;
                """);
            string? deviceId = con.ExecuteScalar<string?>("""
                SELECT Value FROM Setting WHERE GameId = '' AND Key = 'HyperionDeviceId' LIMIT 1;
                """);
            if (string.IsNullOrWhiteSpace(deviceFp) || deviceFp is "0000000000000")
            {
                // 没有可采用的指纹：旧键以后也用不到了，直接删掉。
                DeleteLegacyDeviceKeys(con);
                return;
            }
            info.DeviceFp = deviceFp;
            info.BbsDeviceFp = deviceFp;
            if (Guid.TryParse(deviceId, out _))
            {
                info.BbsDeviceId = deviceId!;
            }
            string updated = JsonSerializer.Serialize(info, DeviceFpJsonContext.Default.AndroidDeviceInfo);
            con.Execute("UPDATE Setting SET Value = @value WHERE GameId = '' AND Key = 'AndroidDeviceInfo';", new { value = updated });
            DeleteLegacyDeviceKeys(con);
            Debug.WriteLine("Adopted the device fingerprint registered by the main branch.");
        }
        catch (Exception ex)
        {
            // 设备信息解析失败时保留旧键，运行时还有机会采用。
            Debug.WriteLine($"Adopt the legacy device info failed: {ex}");
        }
    }



    private static void DeleteLegacyDeviceKeys(SqliteConnection con)
    {
        con.Execute("""
            DELETE FROM Setting
            WHERE Key IN ('HyperionDeviceId', 'HyperionDeviceFp', 'HyperionDeviceFpLastUpdateTime', 'HyperionDeviceAndroidId');
            """);
    }



    private static void DeleteLegacyKey(SqliteConnection con, string key)
    {
        con.Execute("DELETE FROM Setting WHERE GameId = '' AND Key = @key;", new { key });
    }


}
