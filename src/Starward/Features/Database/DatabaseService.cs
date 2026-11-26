using Dapper;
using Microsoft.Data.Sqlite;
using SharpSevenZip;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.Account;
using Starward.Features.Passport;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Starward.Features.Database;

internal static class DatabaseService
{


    private static string _databasePath;


    private static string _connectionString;


    private static Lock _lock = new();


    static DatabaseService()
    {
        SqlMapper.AddTypeHandler(new DapperSqlMapper.DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new DapperSqlMapper.TravelersDiaryPrimogemsMonthGroupStatsListHandler());
        SqlMapper.AddTypeHandler(new DapperSqlMapper.TrailblazeCalendarMonthDataGroupByListHandler());
        SqlMapper.AddTypeHandler(new DapperSqlMapper.StringListHandler());
        SqlMapper.AddTypeHandler(new DapperSqlMapper.GameBizHandler());
    }




    public static SqliteConnection CreateConnection()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        con.Execute("PRAGMA busy_timeout = 5000;");
        return con;
    }



    public static void SetDatabase(string folder)
    {
        if (Directory.Exists(folder))
        {
            _databasePath = Path.GetFullPath(Path.Combine(folder, "StarwardDatabase.db"));
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 10,
            }.ToString();
            InitializeDatabase();
        }
    }



    private static void InitializeDatabase()
    {
        lock (_lock)
        {
            using var con = CreateConnection();
            int version = con.QueryFirstOrDefault<int>("PRAGMA USER_VERSION;");
            if (version == 0)
            {
                con.Execute("PRAGMA JOURNAL_MODE = WAL;");
            }
            // 老 main 分支的数据库同样占用 user_version = 22，但内容是上游的 v21 结构 +
            // 通行证账号表（PassportAccount），而不是上游 v22 的 Setting 表拆分（Key, GameId, Value）。
            // 这类库要从 21 开始重新执行上游的 v22 / v23，再把本分支合并后的 v24 补上。
            bool legacyMainDatabase = version >= 22 && !HasColumn(con, "Setting", "GameId");
            if (legacyMainDatabase)
            {
                Debug.WriteLine("Legacy main database detected (user_version 22 without Setting.GameId), rerunning migrations from v21.");
                BackupLegacyDatabase();
                version = 21;
            }
            foreach (var sql in DatabaseSqls.Skip(version))
            {
                con.Execute(sql);
            }
            // v24 需要按列是否已存在决定要不要 ALTER，SQL 里做不到，因此单独执行。
            // 本分支自己的 v24 / v25 / v26 从未发布，合并成一个 v24。
            if (version <= 23)
            {
                ApplyMigrationV24(con, legacyMainDatabase);
            }
            // 通行证账号存储收口（可重复执行）：把老账号池 PassportAccount 搬进上游表并删表。
            // 放在版本判断之外，是因为本分支的 v24 迁移从未发布过、库里已经有 user_version = 24 的库
            // 也需要补上这一步；做完之后每次启动都是空转。
            PassportAccountMigration.Migrate(con, PassportService.ProtectStoken);
            // 旧配置键清理（同样可重复执行）：这些数据已经搬进启动预设 / 其它表，旧键只会在迁移时读一次，
            // 留着就等于同一份数据有两个 owner，改一个另一个不会变。
            LegacySettingCleanup.Migrate(con);
        }
    }


    /// <summary>
    /// 判断某张表里是否已经有某一列。用于让「加列」类迁移可以重复执行。
    /// </summary>
    private static bool HasColumn(SqliteConnection con, string table, string column)
    {
        try
        {
            return con.Query<string>($"SELECT name FROM pragma_table_info('{table}');")
                      .Contains(column, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 删除若干全局设置行（GameId = ''）。用于迁移后清理旧键：数据已经搬进新表 / 新键，旧键只读一次。
    /// </summary>
    public static void DeleteSettings(params string[] keys)
    {
        if (keys is not { Length: > 0 })
        {
            return;
        }
        try
        {
            using var con = CreateConnection();
            con.Execute("DELETE FROM Setting WHERE GameId = '' AND Key IN @keys;", new { keys });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }



    /// <summary>
    /// 删除某个区服（所有地区 GameId，如 <c>bh3_global-*</c>）下的若干设置行，用于迁移后清理旧键。
    /// </summary>
    public static void DeleteSettingsForGameBiz(GameBiz gameBiz, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(gameBiz.Value) || keys is not { Length: > 0 })
        {
            return;
        }
        try
        {
            using var con = CreateConnection();
            con.Execute("DELETE FROM Setting WHERE GameId GLOB @pattern AND Key IN @keys;", new { pattern = $"{gameBiz.Value}-*", keys });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }


    /// <summary>
    /// 老 main 的库要从 v22 回退重跑上游迁移，先留一份备份，避免迁移失败时用户数据没有退路。
    /// 只在一开始备份一次（文件已存在就跳过）。
    /// <para>用 SQLite 的备份接口而不是复制文件：库可能是 WAL 模式，直接复制 .db 会漏掉还没合并的日志。</para>
    /// </summary>
    private static void BackupLegacyDatabase()
    {
        try
        {
            string backup = _databasePath + ".main-v22.bak";
            if (File.Exists(backup) || !File.Exists(_databasePath))
            {
                return;
            }
            using var backupCon = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backup,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            backupCon.Open();
            using var con = CreateConnection();
            con.BackupDatabase(backupCon);
            Debug.WriteLine($"Backed up the legacy main database to {backup}.");
        }
        catch (Exception ex)
        {
            // 备份失败也继续迁移：用户至少还能通过自动备份恢复。
            Debug.WriteLine(ex);
        }
    }


    /// <summary>
    /// v24：本分支在 0.19 基座上加的一次性迁移。
    /// <para>
    /// 本分支原来的 v24 / v25 / v26 从未发布过，合并成这一步：
    /// </para>
    /// <list type="number">
    /// <item>给 <c>GameRecordRole</c> 补 <c>Aid</c> 列（对应上游 0.19 的数据库结构；上游还会清空角色表，
    /// 本分支保留账号模型所以不做）；</item>
    /// <item>通行证账号存储收口：SToken 只写上游 <c>PassportUserInfo</c> 表，派生令牌只存在工具箱 Cookie 里，
    /// 老账号池表 <c>PassportAccount</c> 迁移后删除（见 <see cref="PassportAccountMigration"/>）；</item>
    /// <item>把 main 的安装路径设置搬进 0.19 的 <c>GameEntry</c> 表，并按旧图标列表恢复固定图标；</item>
    /// <item>从 main 升级上来的一次性设置迁移（当前游戏、工具箱上次选的角色、自动登录总开关）。</item>
    /// </list>
    /// </summary>
    private static void ApplyMigrationV24(SqliteConnection con, bool legacyMainDatabase)
    {
        if (!HasColumn(con, "GameRecordRole", "Aid"))
        {
            con.Execute("ALTER TABLE GameRecordRole ADD COLUMN Aid INTEGER NOT NULL DEFAULT 0;");
        }
        MigrateInstallPathsFromSettings(con);
        MigrateLegacySettings(con, legacyMainDatabase);
        con.Execute("PRAGMA USER_VERSION = 24;");
    }


    /// <summary>
    /// 从 main（0.18）升级上来时补做的一次性设置迁移。
    /// <para>
    /// v22 保留了 main 的旧键，但 0.19 的键名 / 语义变了：当前游戏
    /// （<c>CurrentGameBiz</c> → <c>CurrentGameId</c>）、工具箱上次选的角色、自动登录总开关。
    /// 放在数据库迁移里做，是因为它必须早于任何窗口创建和任何旧进程写库；
    /// 用 <c>LegacyMainDatabaseMigrated</c> 记录是否做过，之后用户在新版本里的选择就再也不会被覆盖。
    /// </para>
    /// <para>
    /// 只对从 main 升级上来的库生效：判断依据是只有本分支 v22 才会保留的小写旧键
    /// （上游 0.19 的 v22 只保留大写开头的键，全新安装也没有这些键），
    /// 所以全新安装和上游 0.19 用户的库不会被误改。
    /// </para>
    /// </summary>
    private static void MigrateLegacySettings(SqliteConnection con, bool legacyMainDatabase)
    {
        try
        {
            string? migrated = con.QueryFirstOrDefault<string>(
                "SELECT Value FROM Setting WHERE Key = 'LegacyMainDatabaseMigrated' AND GameId = '' LIMIT 1;");
            if (!string.Equals(migrated, "1", StringComparison.Ordinal)
                && (legacyMainDatabase || IsLegacyMainDatabase(con)))
            {
                MigrateLegacyCurrentGame(con);
                MigrateLegacyLastSelectGameRecordRole(con);
                MigrateLegacyAuthLoginSetting(con);
                con.Execute("INSERT OR REPLACE INTO Setting (Key, GameId, Value) VALUES ('LegacyMainDatabaseMigrated', '', '1');");
            }
        }
        catch (Exception ex)
        {
            // 迁移属于升级便利功能，失败不能拦住数据库升级。
            Debug.WriteLine(ex);
        }
    }



    /// <summary>
    /// 这个库是不是从 main 升级上来的：只有本分支的 v22 会保留这些非大写开头的旧键
    /// （上游 v22 只保留大写开头的键，上游 0.18→0.19 的用户库里也没有它们）。
    /// </summary>
    private static bool IsLegacyMainDatabase(SqliteConnection con)
    {
        try
        {
            return con.ExecuteScalar<long>("""
                SELECT COUNT(1) FROM Setting
                WHERE GameId = '' AND (
                       Key LIKE 'enable_custom_bg_%'
                    OR Key LIKE 'game_background_ids_%'
                    OR Key LIKE 'use_popup_window_%'
                    OR Key LIKE 'version_poster_%'
                    OR Key LIKE 'last_select_game_record_role_%'
                    OR Key LIKE 'last_region_of_%');
                """) > 0;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 当前游戏：main 按区服记（<c>CurrentGameBiz</c>），0.19 按游戏 ID 记（<c>CurrentGameId</c>）。
    /// <para>
    /// 升级前的真值优先从 v22 迁移前留下的备份库读：用户可能用旧版 0.18 又打开过迁移后的库，
    /// 把 <c>CurrentGameBiz</c> 改成了别的区服（旧版不认新表结构，但读写老键仍然“正常”）。
    /// 没有备份时才退回读 Setting 里的旧键。
    /// </para>
    /// <para>
    /// 只在「还没选过游戏」或「能从迁移前备份读到真值」时写，避免覆盖用户在新版本里的选择。
    /// </para>
    /// </summary>
    private static void MigrateLegacyCurrentGame(SqliteConnection con)
    {
        try
        {
            string? currentGameId = con.QueryFirstOrDefault<string>(
                "SELECT Value FROM Setting WHERE Key = 'CurrentGameId' AND GameId = '' LIMIT 1;");
            string? fromBackup = ReadLegacyCurrentGameBizFromBackup();
            string? legacyGameBiz = fromBackup;
            if (string.IsNullOrWhiteSpace(legacyGameBiz))
            {
                legacyGameBiz = con.QueryFirstOrDefault<string>(
                    "SELECT Value FROM Setting WHERE Key = 'CurrentGameBiz' AND GameId = '' LIMIT 1;");
            }
            if (string.IsNullOrWhiteSpace(legacyGameBiz) || !GameBiz.TryParse(legacyGameBiz, out GameBiz gameBiz))
            {
                return;
            }
            if (GameId.FromGameBiz(gameBiz) is not GameId gameId)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(currentGameId) && string.IsNullOrWhiteSpace(fromBackup))
            {
                return;
            }
            con.Execute("INSERT OR REPLACE INTO Setting (Key, GameId, Value) VALUES ('CurrentGameId', '', @id);", new { id = gameId.Id });
            // 让这个游戏在 GameEntry 里存在且固定，启动器才有图标可恢复（没装游戏时也先固定，图标上可以定位）。
            string? launcherId = LauncherId.FromGameBiz(gameBiz);
            if (!string.IsNullOrWhiteSpace(launcherId))
            {
                int order = con.ExecuteScalar<int>("SELECT IFNULL(MAX(PinnedOrder), 0) + 1 FROM GameEntry;");
                con.Execute("""
                    INSERT INTO GameEntry (GameId, GameBiz, LauncherId, InstallPath, Removable, IsPinned, PinnedOrder)
                    VALUES (@id, @gameBiz, @launcherId, NULL, 0, 1, @order)
                    ON CONFLICT(GameId) DO UPDATE SET IsPinned = 1;
                    """, new { id = gameId.Id, gameBiz = gameBiz.Value, launcherId, order });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }


    /// <summary>
    /// 读 v22 迁移前留下的备份库里的 main 当前游戏（<c>CurrentGameBiz</c>）。备份不存在或读不到时返回 null。
    /// </summary>
    private static string? ReadLegacyCurrentGameBizFromBackup()
    {
        try
        {
            string backup = _databasePath + ".main-v22.bak";
            if (!File.Exists(backup))
            {
                return null;
            }
            using var con = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backup,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            con.Open();
            con.Execute("PRAGMA busy_timeout = 5000;");
            return con.QueryFirstOrDefault<string>("SELECT Value FROM Setting WHERE Key = 'CurrentGameBiz' LIMIT 1;");
        }
        catch
        {
            return null;
        }
    }


    /// <summary>
    /// 工具箱「上次选的角色」：main 的键是 <c>last_select_game_record_role_{GameBiz}</c>（全局），
    /// 0.19 改成 <c>LastSelectGameRecordRole</c> + <c>GameId = {GameBiz}</c>。
    /// </summary>
    private static void MigrateLegacyLastSelectGameRecordRole(SqliteConnection con)
    {
        try
        {
            con.Execute("""
                INSERT OR REPLACE INTO Setting (Key, GameId, Value)
                SELECT 'LastSelectGameRecordRole',
                       substr(Key, length('last_select_game_record_role_') + 1),
                       Value
                FROM Setting
                WHERE GameId = '' AND Key LIKE 'last_select_game_record_role_%';
                """);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }


    /// <summary>
    /// 自动登录总开关：main 是默认开启的 <c>EnableAuthTicketLoginV2</c>，
    /// 0.19 换成默认关闭的 <c>AuthLoginGameByPassportAccount</c>。新键已经有行时不动。
    /// </summary>
    private static void MigrateLegacyAuthLoginSetting(SqliteConnection con)
    {
        try
        {
            string? existing = con.QueryFirstOrDefault<string>(
                "SELECT Value FROM Setting WHERE Key = 'AuthLoginGameByPassportAccount' AND GameId = '' LIMIT 1;");
            if (existing is not null)
            {
                return;
            }
            string? legacy = con.QueryFirstOrDefault<string>(
                "SELECT Value FROM Setting WHERE Key = 'EnableAuthTicketLoginV2' AND GameId = '' LIMIT 1;");
            // 旧键没写过时旧版本的默认值是开启；明确写过 False 的按 False 迁移。
            bool enabled = !string.Equals(legacy?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            con.Execute("INSERT OR REPLACE INTO Setting (Key, GameId, Value) VALUES ('AuthLoginGameByPassportAccount', '', @value);",
                new { value = enabled ? "True" : "False" });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }



    /// <summary>
    /// 把 main 中按 <c>install_path_{GameBiz}</c> / <c>install_path_removable_{GameBiz}</c> 保存的安装路径
    /// 迁移到 0.19 的 <c>GameEntry</c> 表（v22 已把它们重命名成 InstallPath / InstallPathRemovable 设置键）。
    /// <para>
    /// 只补全，不覆盖：<c>GameEntry</c> 里已经有安装路径的条目（用户自己在 0.19 里定位过的）保持原样。
    /// 迁移完成后清掉这些过渡设置键。
    /// </para>
    /// </summary>
    private static void MigrateInstallPathsFromSettings(SqliteConnection con)
    {
        try
        {
            var rows = con.Query<SettingRow>("""
                SELECT GameId, Value FROM Setting
                WHERE Key = 'InstallPath' AND Value IS NOT NULL AND Value <> '';
                """).AsList();
            foreach (SettingRow row in rows)
            {
                string settingGameId = row.GameId;
                // 0.19 的 Setting.GameId 形如 "hk4e_cn-1Z8W5NHUQb"（GameBiz-Id），GameBiz 本身不含 '-'。
                int index = settingGameId.IndexOf('-');
                if (index <= 0 || index >= settingGameId.Length - 1)
                {
                    continue;
                }
                GameBiz gameBiz = new(settingGameId[..index]);
                string id = settingGameId[(index + 1)..];
                string? launcherId = LauncherId.FromGameBiz(gameBiz);
                if (string.IsNullOrWhiteSpace(launcherId))
                {
                    continue;
                }
                SettingRow? removableRow = con.QueryFirstOrDefault<SettingRow>(
                    "SELECT GameId, Value FROM Setting WHERE Key = 'InstallPathRemovable' AND GameId = @settingGameId LIMIT 1;",
                    new { settingGameId });
                bool removable = removableRow?.Value?.Trim().ToLowerInvariant() is "true" or "1";
                // ON CONFLICT 只在原有 InstallPath 为空时写入，绝不覆盖用户手动定位过的路径。
                con.Execute("""
                    INSERT INTO GameEntry (GameId, GameBiz, LauncherId, InstallPath, Removable, IsPinned, PinnedOrder)
                    VALUES (@id, @gameBiz, @launcherId, @installPath, @removable, 0, 0)
                    ON CONFLICT(GameId) DO UPDATE SET LauncherId  = COALESCE(NULLIF(GameEntry.LauncherId, ''), excluded.LauncherId),
                                              InstallPath = COALESCE(GameEntry.InstallPath, excluded.InstallPath),
                                              Removable   = CASE WHEN GameEntry.InstallPath IS NULL
                                                                 THEN excluded.Removable
                                                                 ELSE GameEntry.Removable END;
                    """, new { id, gameBiz = gameBiz.Value, launcherId, installPath = row.Value, removable });
                con.Execute("DELETE FROM Setting WHERE Key IN ('InstallPath', 'InstallPathRemovable') AND GameId = @settingGameId;", new { settingGameId });
            }
            RestorePinnedGameIcons(con);
        }
        catch (Exception ex)
        {
            // 安装路径迁移属于升级便利功能，失败不能拦住数据库升级。
            Debug.WriteLine(ex);
        }
    }


    /// <summary>
    /// main 里「游戏选择器里的图标列表」是全局设置 <c>SelectedGameBizs</c>（逗号分隔的区服），
    /// 「固定」开关 <c>IsGameBizSelectorPinned</c> 只决定那列图标是否常驻展开。
    /// 0.19 把这两件事合并成 <c>GameEntry.IsPinned</c>，所以优先按旧列表逐一把对应条目固定住，
    /// 顺序沿用旧列表；旧列表里有、但还没定位安装路径的游戏也建条目（0.19 的图标上可以直接定位/安装）。
    /// 读不到旧列表时，退回「旧固定开关打开就把所有已定位的游戏固定住」的行为。
    /// </summary>
    private static void RestorePinnedGameIcons(SqliteConnection con)
    {
        try
        {
            if (TryRestorePinnedGameIconsFromSelectedList(con))
            {
                return;
            }
            string? pinned = con.QueryFirstOrDefault<string>("""
                SELECT Value FROM Setting
                WHERE Key IN ('IsGameBizSelectorPinned', 'is_game_biz_selector_pinned') AND GameId = ''
                LIMIT 1;
                """);
            if (pinned is null || pinned.Trim().ToLowerInvariant() is not ("true" or "1"))
            {
                return;
            }
            var entries = con.Query<GameEntryRow>("""
                SELECT GameId, GameBiz FROM GameEntry
                WHERE InstallPath IS NOT NULL AND InstallPath <> ''
                ORDER BY GameBiz, LauncherId, GameId;
                """).AsList();
            int order = 1;
            foreach (GameEntryRow entry in entries)
            {
                con.Execute("UPDATE GameEntry SET IsPinned = 1, PinnedOrder = @order WHERE GameId = @gameId;", new { order, gameId = entry.GameId });
                order++;
            }
            con.Execute("DELETE FROM Setting WHERE Key IN ('IsGameBizSelectorPinned', 'is_game_biz_selector_pinned');");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }


    /// <summary>
    /// 按 main 的「已选游戏区服」列表固定 0.19 的游戏图标；没有这份旧列表时返回 <see langword="false"/>。
    /// </summary>
    /// <param name="con">数据库连接。</param>
    /// <returns>是否读到了可用的旧列表（读到即已迁移完成）。</returns>
    private static bool TryRestorePinnedGameIconsFromSelectedList(SqliteConnection con)
    {
        string? selected = con.QueryFirstOrDefault<string>("""
            SELECT Value FROM Setting
            WHERE Key IN ('SelectedGameBizs', 'selected_game_bizs') AND GameId = ''
            LIMIT 1;
            """);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return false;
        }
        var gameBizs = new List<GameBiz>();
        foreach (string str in selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (GameBiz.TryParse(str, out GameBiz biz) && !gameBizs.Contains(biz))
            {
                gameBizs.Add(biz);
            }
        }
        if (gameBizs.Count == 0)
        {
            return false;
        }
        int order = 1;
        foreach (GameBiz gameBiz in gameBizs)
        {
            if (GameId.FromGameBiz(gameBiz) is not GameId gameId)
            {
                continue;
            }
            string? launcherId = LauncherId.FromGameBiz(gameBiz);
            if (string.IsNullOrWhiteSpace(launcherId))
            {
                continue;
            }
            // 只补顺序与固定标记：已有安装路径的条目保持原样，用户之后在 0.19 里调整过顺序也不受影响。
            con.Execute("""
                INSERT INTO GameEntry (GameId, GameBiz, LauncherId, InstallPath, Removable, IsPinned, PinnedOrder)
                VALUES (@gameId, @gameBiz, @launcherId, NULL, 0, 1, @order)
                ON CONFLICT(GameId) DO UPDATE SET IsPinned    = 1,
                                                 PinnedOrder  = @order,
                                                 LauncherId   = COALESCE(NULLIF(GameEntry.LauncherId, ''), excluded.LauncherId);
                """, new { gameId = gameId.Id, gameBiz = gameBiz.Value, launcherId, order });
            order++;
        }
        con.Execute("DELETE FROM Setting WHERE Key IN ('SelectedGameBizs', 'selected_game_bizs', 'IsGameBizSelectorPinned', 'is_game_biz_selector_pinned');");
        return true;
    }


    /// <summary>迁移过程中读写 Setting 行用的临时模型。</summary>
    private sealed class SettingRow
    {
        public string GameId { get; set; } = "";

        public string Value { get; set; } = "";
    }


    /// <summary>迁移过程中读写 GameEntry 行用的临时模型。</summary>
    private sealed class GameEntryRow
    {
        public string GameId { get; set; } = "";

        public string GameBiz { get; set; } = "";
    }



    public static void BackupDatabase(string file)
    {
        using var backupCon = new SqliteConnection($"DataSource={file}; Pooling=False;");
        backupCon.Open();
        using var con = CreateConnection();
        con.Execute("VACUUM;", commandType: CommandType.Text);
        con.BackupDatabase(backupCon);
    }



    public static void AutoBackupToAppDataLocal()
    {
        try
        {
#if DEBUG
            return;
#endif
#pragma warning disable CS0162 // 检测到无法访问的代码
            string folder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Starward\DatabaseBackup");
#pragma warning restore CS0162 // 检测到无法访问的代码
            Directory.CreateDirectory(folder);
            string hash = Convert.ToHexStringLower(XxHash3.Hash(MemoryMarshal.AsBytes(AppConfig.UserDataFolder.AsSpan())));
            string file = Path.Combine(folder, $"StarwardDatabase_AutoBackup_{hash}_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            string archive = Path.ChangeExtension(file, ".7z");
            string archive_tmp = archive + ".tmp";
            string[] files = Directory.GetFiles(folder, $"StarwardDatabase_AutoBackup_{hash}_*.7z");
            if (files.Length == 0)
            {
                BackupDatabase(file);
                new SharpSevenZipCompressor().CompressFiles(archive_tmp, file);
                File.Move(archive_tmp, archive, true);
            }
            else
            {
                string last = files.OrderByDescending(File.GetLastWriteTime).First();
                if (DateTime.Now - File.GetLastWriteTime(last) > TimeSpan.FromDays(7))
                {
                    BackupDatabase(file);
                    new SharpSevenZipCompressor().CompressFiles(archive_tmp, file);
                    File.Move(archive_tmp, archive, true);
                    File.Delete(last);
                }
            }
            files = Directory.GetFiles(folder, $"StarwardDatabase_AutoBackup_{hash}_*.db");
            foreach (string item in files)
            {
                File.Delete(item);
            }
            files = Directory.GetFiles(folder, $"StarwardDatabase_AutoBackup_{hash}_*.tmp");
            foreach (string item in files)
            {
                File.Delete(item);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }




    private class KVT
    {

        public KVT() { }


        public KVT(string key, string value, DateTime time)
        {
            Key = key;
            Value = value;
            Time = time;
        }

        public string Key { get; set; }

        public string Value { get; set; }

        public DateTime Time { get; set; }
    }





    public static T? GetValue<T>(string key, out DateTime time, T? defaultValue = default)
    {
        time = DateTime.MinValue;
        try
        {
            using var con = CreateConnection();
            var kvt = con.QueryFirstOrDefault<KVT>("SELECT * FROM KVT WHERE Key = @key LIMIT 1;", new { key });
            if (kvt != null)
            {
                time = kvt.Time;
                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter == null)
                {
                    return defaultValue;
                }
                return (T?)converter.ConvertFromString(kvt.Value);
            }
            else
            {
                return defaultValue;
            }
        }
        catch
        {
            return defaultValue;
        }
    }



    public static bool TryGetValue<T>(string key, out T? result, out DateTime time, T? defaultValue = default)
    {
        result = defaultValue;
        time = DateTime.MinValue;
        try
        {
            using var con = CreateConnection();
            var kvt = con.QueryFirstOrDefault<KVT>("SELECT * FROM KVT WHERE Key = @key LIMIT 1;", new { key });
            if (kvt != null)
            {
                time = kvt.Time;
                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter == null)
                {
                    return false;
                }
                result = (T?)converter.ConvertFromString(kvt.Value);
                return true;
            }
            else
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }



    public static void SetValue<T>(string key, T value, DateTime? time = null)
    {
        try
        {
            using var con = CreateConnection();
            con.Execute("INSERT OR REPLACE INTO KVT (Key, Value, Time) VALUES (@Key, @Value, @Time);", new KVT(key, value?.ToString() ?? "", time ?? DateTime.Now));

        }
        catch { }
    }








    #region Database Structure


    private static readonly List<string> DatabaseSqls =
    [
        Sql_v1,
        Sql_v2,
        Sql_v3,
        Sql_v4,
        Sql_v5,
        Sql_v6,
        Sql_v7,
        Sql_v8,
        Sql_v9,
        Sql_v10,
        Sql_v11,
        Sql_v12,
        Sql_v13,
        Sql_v14,
        Sql_v15,
        Sql_v16,
        Sql_v17,
        Sql_v18,
        Sql_v19,
        Sql_v20,
        Sql_v21,
        Sql_v22,
        Sql_v23,
    ];


    private const string Sql_v1 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS KVT
        (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL,
            Time  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS GameAccount
        (
            SHA256  TEXT    NOT NULL PRIMARY KEY,
            GameBiz INTEGER NOT NULL,
            Uid     INTEGER NOT NULL,
            Name    TEXT    NOT NULL,
            Value   BLOB    NOT NULL,
            Time    TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_GameAccount_GameBiz ON GameAccount (GameBiz);

        CREATE TABLE IF NOT EXISTS GachaLogUrl
        (
            GameBiz INTEGER NOT NULL,
            Uid     INTEGER NOT NULL,
            Url     TEXT    NOT NULL,
            Time    TEXT    NOT NULL,
            PRIMARY KEY (GameBiz, Uid)
        );

        CREATE TABLE IF NOT EXISTS GenshinGachaItem
        (
            Uid       INTEGER NOT NULL,
            Id        INTEGER NOT NULL,
            Name      TEXT    NOT NULL,
            Time      TEXT    NOT NULL,
            ItemId    INTEGER NOT NULL,
            ItemType  TEXT    NOT NULL,
            RankType  INTEGER NOT NULL,
            GachaType INTEGER NOT NULL,
            Count     INTEGER NOT NULL,
            Lang      TEXT,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_GenshinGachaItem_Id ON GenshinGachaItem (Id);
        CREATE INDEX IF NOT EXISTS IX_GenshinGachaItem_RankType ON GenshinGachaItem (RankType);
        CREATE INDEX IF NOT EXISTS IX_GenshinGachaItem_GachaType ON GenshinGachaItem (GachaType);

        CREATE TABLE IF NOT EXISTS StarRailGachaItem
        (
            Uid       INTEGER NOT NULL,
            Id        INTEGER NOT NULL,
            Name      TEXT    NOT NULL,
            Time      TEXT    NOT NULL,
            ItemId    INTEGER NOT NULL,
            ItemType  TEXT    NOT NULL,
            RankType  INTEGER NOT NULL,
            GachaType INTEGER NOT NULL,
            GachaId   INTEGER NOT NULL,
            Count     INTEGER NOT NULL,
            Lang      TEXT,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_StarRailGachaItem_Id ON StarRailGachaItem (Id);
        CREATE INDEX IF NOT EXISTS IX_StarRailGachaItem_RankType ON StarRailGachaItem (RankType);
        CREATE INDEX IF NOT EXISTS IX_StarRailGachaItem_GachaType ON StarRailGachaItem (GachaType);

        PRAGMA USER_VERSION = 1;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v2 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS PlayTimeItem
        (
            TimeStamp INTEGER PRIMARY KEY,
            GameBiz   INTEGER NOT NULL,
            Pid       INTEGER NOT NULL,
            State     INTEGER NOT NULL,
            CursorPos INTEGER NOT NULL,
            Message   TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_PlayTimeItem_GameBiz ON PlayTimeItem(GameBiz);
        CREATE INDEX IF NOT EXISTS IX_PlayTimeItem_Pid ON PlayTimeItem(Pid);
        CREATE INDEX IF NOT EXISTS IX_PlayTimeItem_State ON PlayTimeItem(State);

        PRAGMA USER_VERSION = 2;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v3 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS GameAccount_dg_tmp
        (
            SHA256  TEXT    NOT NULL,
            GameBiz INTEGER NOT NULL,
            Uid     INTEGER NOT NULL,
            Name    TEXT    NOT NULL,
            Value   BLOB    NOT NULL,
            Time    TEXT    NOT NULL,
            PRIMARY KEY (SHA256, GameBiz)
        );

        INSERT INTO GameAccount_dg_tmp(SHA256, GameBiz, Uid, Name, Value, Time)
        SELECT SHA256, GameBiz, Uid, Name, Value, Time
        FROM GameAccount;

        DROP TABLE IF EXISTS GameAccount;
        ALTER TABLE GameAccount_dg_tmp RENAME TO GameAccount;
        CREATE INDEX IF NOT EXISTS IX_GameAccount_GameBiz ON GameAccount (GameBiz);

        PRAGMA USER_VERSION = 3;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v4 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS Setting
        (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT
        );

        CREATE TABLE IF NOT EXISTS GameRecordUser
        (
            Uid       INTEGER NOT NULL,
            IsHoyolab INTEGER NOT NULL,
            Nickname  TEXT,
            Avatar    TEXT,
            Introduce TEXT,
            Gender    INTEGER NOT NULL,
            AvatarUrl TEXT,
            Pendant   TEXT,
            Cookie    TEXT,
            PRIMARY KEY (Uid, IsHoyolab)
        );

        CREATE TABLE IF NOT EXISTS GameRecordRole
        (
            Uid        INTEGER NOT NULL,
            GameBiz    TEXT    NOT NULL,
            Nickname   TEXT,
            Level      INTEGER NOT NULL,
            Region     TEXT    NOT NULL,
            RegionName TEXT,
            Cookie     TEXT,
            PRIMARY KEY (Uid, GameBiz)
        );
        CREATE INDEX IF NOT EXISTS IX_GameRecordRole_GameBiz ON GameRecordRole (GameBiz);

        CREATE TABLE IF NOT EXISTS SpiralAbyssInfo
        (
            Uid              INTEGER NOT NULL,
            ScheduleId       INTEGER NOT NULL,
            StartTime        TEXT    NOT NULL,
            EndTime          TEXT    NOT NULL,
            TotalBattleCount INTEGER NOT NULL,
            TotalWinCount    INTEGER NOT NULL,
            MaxFloor         TEXT,
            TotalStar        INTEGER NOT NULL,
            Value            TEXT    NOT NULL,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_SpiralAbyssInfo_ScheduleId ON SpiralAbyssInfo (ScheduleId);

        CREATE TABLE IF NOT EXISTS TravelersDiaryMonthData
        (
            Uid                   INTEGER NOT NULL,
            Year                  INTEGER NOT NULL,
            Month                 INTEGER NOT NULL,
            CurrentPrimogems      INTEGER NOT NULL,
            CurrentMora           INTEGER NOT NULL,
            LastPrimogems         INTEGER NOT NULL,
            LastMora              INTEGER NOT NULL,
            CurrentPrimogemsLevel INTEGER NOT NULL,
            PrimogemsChangeRate   INTEGER NOT NULL,
            MoraChangeRate        INTEGER NOT NULL,
            PrimogemsGroupBy      TEXT    NOT NULL,
            PRIMARY KEY (Uid, Year, Month)
        );

        CREATE TABLE IF NOT EXISTS TravelersDiaryAwardItem
        (
            Id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Uid        INTEGER NOT NULL,
            Year       INTEGER NOT NULL,
            Month      INTEGER NOT NULL,
            Type       INTEGER NOT NULL,
            ActionId   INTEGER NOT NULL,
            ActionName TEXT,
            Time       TEXT    NOT NULL,
            Number     INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_TravelersDiaryAwardItem_Uid_Year_Month ON TravelersDiaryAwardItem (Uid, Year, Month);
        CREATE INDEX IF NOT EXISTS IX_TravelersDiaryAwardItem_Type ON TravelersDiaryAwardItem (Type);
        CREATE INDEX IF NOT EXISTS IX_TravelersDiaryAwardItem_Time ON TravelersDiaryAwardItem (Time);

        CREATE TABLE IF NOT EXISTS ForgottenHallInfo
        (
            Uid        INTEGER NOT NULL,
            ScheduleId INTEGER NOT NULL,
            BeginTime  TEXT    NOT NULL,
            EndTime    TEXT    NOT NULL,
            StarNum    INTEGER NOT NULL,
            MaxFloor   TEXT,
            BattleNum  INTEGER NOT NULL,
            HasData    INTEGER NOT NULL,
            Value      TEXT    NOT NULL,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_ForgottenHallInfo_ScheduleId ON ForgottenHallInfo (ScheduleId);

        CREATE TABLE IF NOT EXISTS SimulatedUniverseRecord
        (
            Uid           INTEGER NOT NULL,
            ScheduleId    INTEGER NOT NULL,
            FinishCount   INTEGER NOT NULL,
            ScheduleBegin TEXT    NOT NULL,
            ScheduleEnd   TEXT    NOT NULL,
            HasData       INTEGER NOT NULL,
            Value         TEXT    NOT NULL,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_SimulatedUniverseRecord_ScheduleId ON SimulatedUniverseRecord (ScheduleId);

        CREATE TABLE IF NOT EXISTS TrailblazeCalendarMonthData
        (
            Uid              INTEGER NOT NULL,
            Month            TEXT    NOT NULL,
            CurrentHcoin     INTEGER NOT NULL,
            CurrentRailsPass INTEGER NOT NULL,
            LastHcoin        INTEGER NOT NULL,
            LastRailsPass    INTEGER NOT NULL,
            HcoinRate        INTEGER NOT NULL,
            RailsRate        INTEGER NOT NULL,
            GroupBy          TEXT    NOT NULL,
            PRIMARY KEY (Uid, Month)
        );

        CREATE TABLE IF NOT EXISTS TrailblazeCalendarDetailItem
        (
            Id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Uid        INTEGER NOT NULL,
            Month      TEXT    NOT NULL,
            Type       INTEGER NOT NULL,
            Action     TEXT    NOT NULL,
            ActionName TEXT    NOT NULL,
            Time       TEXT    NOT NULL,
            Number     INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_TrailblazeCalendarDetailItem_Uid_Month ON TrailblazeCalendarDetailItem (Uid, Month);
        CREATE INDEX IF NOT EXISTS IX_TrailblazeCalendarDetailItem_Type ON TrailblazeCalendarDetailItem (Type);
        CREATE INDEX IF NOT EXISTS IX_TrailblazeCalendarDetailItem_Time ON TrailblazeCalendarDetailItem (Time);

        PRAGMA USER_VERSION = 4;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v5 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS GenshinGachaInfo
        (
            Id          INTEGER NOT NULL PRIMARY KEY,
            Name        TEXT,
            Icon        TEXT,
            Element     INTEGER NOT NULL,
            Level       INTEGER NOT NULL,
            CatId       INTEGER NOT NULL,
            WeaponCatId INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_GenshinGachaInfo_Name ON GenshinGachaInfo (Name);

        CREATE TABLE IF NOT EXISTS StarRailGachaInfo
        (
            ItemId         INTEGER NOT NULL PRIMARY KEY,
            ItemName       TEXT,
            IconUrl        TEXT,
            DamageType     INTEGER NOT NULL,
            Rarity         INTEGER NOT NULL,
            AvatarBaseType INTEGER NOT NULL,
            WikiUrl        TEXT,
            IsSystem       INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_StarRailGachaInfo_Name ON StarRailGachaInfo (ItemName);

        PRAGMA USER_VERSION = 5;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v6 = """
        BEGIN TRANSACTION;

        CREATE INDEX IF NOT EXISTS IX_GenshinGachaItem_Name ON GenshinGachaItem (Name);
        CREATE INDEX IF NOT EXISTS IX_GenshinGachaItem_ItemId ON GenshinGachaItem (ItemId);
        CREATE INDEX IF NOT EXISTS IX_StarRailGachaItem_Name ON StarRailGachaItem (Name);
        CREATE INDEX IF NOT EXISTS IX_StarRailGachaItem_ItemId ON StarRailGachaItem (ItemId);

        PRAGMA USER_VERSION = 6;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v7 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS GenshinQueryItem
        (
            Uid      INTEGER NOT NULL,
            Id       INTEGER NOT NULL,
            AddNum   INTEGER NOT NULL,
            Reason   TEXT,
            DateTime TEXT,
            Type     INTEGER NOT NULL,
            Icon     TEXT,
            Level    INTEGER NOT NULL,
            Quality  INTEGER NOT NULL,
            Name     TEXT,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_GenshinQueryItem_Id ON GenshinQueryItem (Id);
        CREATE INDEX IF NOT EXISTS IX_GenshinQueryItem_AddNum ON GenshinQueryItem (AddNum);
        CREATE INDEX IF NOT EXISTS IX_GenshinQueryItem_Reason ON GenshinQueryItem (Reason);
        CREATE INDEX IF NOT EXISTS IX_GenshinQueryItem_DateTime ON GenshinQueryItem (DateTime);
        CREATE INDEX IF NOT EXISTS IX_GenshinQueryItem_Type ON GenshinQueryItem (Type);

        CREATE TABLE IF NOT EXISTS StarRailQueryItem
        (
            Id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Uid             INTEGER NOT NULL,
            Type            INTEGER NOT NULL,
            Action          TEXT,
            AddNum          INTEGER NOT NULL,
            Time            TEXT,
            RelicName       TEXT,
            RelicLevel      INTEGER NOT NULL,
            RelicRarity     INTEGER NOT NULL,
            EquipmentName   TEXT,
            EquipmentLevel  INTEGER NOT NULL,
            EquipmentRarity INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_StarRailQueryItem_Uid ON StarRailQueryItem (Uid);
        CREATE INDEX IF NOT EXISTS IX_StarRailQueryItem_Type ON StarRailQueryItem (Type);
        CREATE INDEX IF NOT EXISTS IX_StarRailQueryItem_Action ON StarRailQueryItem (Action);
        CREATE INDEX IF NOT EXISTS IX_StarRailQueryItem_AddNum ON StarRailQueryItem (AddNum);
        CREATE INDEX IF NOT EXISTS IX_StarRailQueryItem_Time ON StarRailQueryItem (Time);

        PRAGMA USER_VERSION = 7;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v8 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS PureFictionInfo
        (
            Uid        INTEGER NOT NULL,
            ScheduleId INTEGER NOT NULL,
            BeginTime  TEXT    NOT NULL,
            EndTime    TEXT    NOT NULL,
            StarNum    INTEGER NOT NULL,
            MaxFloor   TEXT,
            BattleNum  INTEGER NOT NULL,
            HasData    INTEGER NOT NULL,
            Value      TEXT    NOT NULL,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_PureFictionInfo_ScheduleId ON PureFictionInfo (ScheduleId);

        PRAGMA USER_VERSION = 8;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v9 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS ZZZGachaItem
        (
            Uid       INTEGER NOT NULL,
            Id        INTEGER NOT NULL,
            Name      TEXT    NOT NULL,
            Time      TEXT    NOT NULL,
            ItemId    INTEGER NOT NULL,
            ItemType  TEXT    NOT NULL,
            RankType  INTEGER NOT NULL,
            GachaType INTEGER NOT NULL,
            Count     INTEGER NOT NULL,
            Lang      TEXT,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_ZZZGachaItem_Id ON ZZZGachaItem (Id);
        CREATE INDEX IF NOT EXISTS IX_ZZZGachaItem_RankType ON ZZZGachaItem (RankType);
        CREATE INDEX IF NOT EXISTS IX_ZZZGachaItem_GachaType ON ZZZGachaItem (GachaType);

        CREATE TABLE IF NOT EXISTS ApocalypticShadowInfo
        (
            Uid           INTEGER NOT NULL,
            ScheduleId    INTEGER NOT NULL,
            BeginTime     TEXT    NOT NULL,
            EndTime       TEXT    NOT NULL,
            UpperBossIcon TEXT    NOT NULL,
            LowerBossIcon TEXT    NOT NULL,
            StarNum       INTEGER NOT NULL,
            MaxFloor      TEXT,
            BattleNum     INTEGER NOT NULL,
            HasData       INTEGER NOT NULL,
            Value         TEXT    NOT NULL,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_ApocalypticShadowInfo_ScheduleId ON ApocalypticShadowInfo (ScheduleId);

        PRAGMA USER_VERSION = 9;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v10 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS ZZZQueryItem
        (
            Id           INTEGER NOT NULL,
            Uid          INTEGER NOT NULL,
            Type         INTEGER NOT NULL,
            Reason       TEXT,
            AddNum       INTEGER NOT NULL,
            DateTime     TEXT,
            EquipName    TEXT,
            EquipRarity  INTEGER NOT NULL,
            EquipLevel   INTEGER NOT NULL,
            WeaponName   TEXT,
            WeaponRarity INTEGER NOT NULL,
            WeaponLevel  INTEGER NOT NULL,
            ClientIp     TEXT,
            ActionName   TEXT,
            CardType     INTEGER NOT NULL,
            ItemName     TEXT,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_ZZZQueryItem_Id ON ZZZQueryItem (Id);
        CREATE INDEX IF NOT EXISTS IX_ZZZQueryItem_Type ON ZZZQueryItem (Type);
        CREATE INDEX IF NOT EXISTS IX_ZZZQueryItem_Reason ON ZZZQueryItem (Reason);
        CREATE INDEX IF NOT EXISTS IX_ZZZQueryItem_AddNum ON ZZZQueryItem (AddNum);
        CREATE INDEX IF NOT EXISTS IX_ZZZQueryItem_DateTime ON ZZZQueryItem (DateTime);

        CREATE TABLE IF NOT EXISTS ImaginariumTheaterInfo
        (
            Uid          INTEGER NOT NULL,
            ScheduleId   INTEGER NOT NULL,
            StartTime    TEXT,
            EndTime      TEXT,
            DifficultyId INTEGER NOT NULL,
            MaxRoundId   INTEGER NOT NULL,
            Heraldry     INTEGER NOT NULL,
            MedalNum     INTEGER NOT NULL,
            Value        TEXT,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_ImaginariumTheaterInfo_ScheduleId ON ImaginariumTheaterInfo (ScheduleId);

        PRAGMA USER_VERSION = 10;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v11 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS InterKnotReportSummary
        (
            Uid       INTEGER NOT NULL,
            DataMonth TEXT    NOT NULL,
            Value     TEXT,
            PRIMARY KEY (Uid, DataMonth)
        );

        CREATE TABLE IF NOT EXISTS InterKnotReportDetailItem
        (
            Uid       INTEGER NOT NULL,
            Id        INTEGER NOT NULL,
            DataMonth TEXT    NOT NULL,
            DataType  TEXT    NOT NULL,
            Action    TEXT    NOT NULL,
            Time      TEXT    NOT NULL,
            Number       INTEGER NOT NULL,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_InterKnotReportDetailItem_DataMonth ON InterKnotReportDetailItem (DataMonth);
        CREATE INDEX IF NOT EXISTS IX_InterKnotReportDetailItem_DataType ON InterKnotReportDetailItem (DataType);
        CREATE INDEX IF NOT EXISTS IX_InterKnotReportDetailItem_Time ON InterKnotReportDetailItem (Time);

        PRAGMA USER_VERSION = 11;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v12 = """
        BEGIN TRANSACTION;

        UPDATE PlayTimeItem SET GameBiz = 'hk4e_cn' WHERE GameBiz IN (11, 13);
        UPDATE PlayTimeItem SET GameBiz = 'hk4e_global' WHERE GameBiz = 12;
        UPDATE PlayTimeItem SET GameBiz = 'hk4e_bilibili' WHERE GameBiz = 14;
        UPDATE PlayTimeItem SET GameBiz = 'hkrpg_cn' WHERE GameBiz = 21;
        UPDATE PlayTimeItem SET GameBiz = 'hkrpg_global' WHERE GameBiz = 22;
        UPDATE PlayTimeItem SET GameBiz = 'hkrpg_bilibili' WHERE GameBiz = 24;
        UPDATE PlayTimeItem SET GameBiz = 'bh3_cn' WHERE GameBiz = 31;
        UPDATE PlayTimeItem SET GameBiz = 'bh3_global' WHERE GameBiz IN (32, 33, 34, 35, 36);
        UPDATE PlayTimeItem SET GameBiz = 'nap_cn' WHERE GameBiz = 41;
        UPDATE PlayTimeItem SET GameBiz = 'nap_global' WHERE GameBiz = 42;
        UPDATE PlayTimeItem SET GameBiz = 'nap_bilibili' WHERE GameBiz = 44;

        CREATE TABLE IF NOT EXISTS PlayTimeItem_dg_tmp
        (
            TimeStamp INTEGER PRIMARY KEY,
            GameBiz   TEXT    NOT NULL,
            Pid       INTEGER NOT NULL,
            State     INTEGER NOT NULL,
            CursorPos INTEGER NOT NULL,
            Message   TEXT
        );
        INSERT INTO PlayTimeItem_dg_tmp(TimeStamp, GameBiz, Pid, State, CursorPos, Message) SELECT TimeStamp, GameBiz, Pid, State, CursorPos, Message FROM PlayTimeItem;
        DROP TABLE IF EXISTS PlayTimeItem;
        ALTER TABLE PlayTimeItem_dg_tmp RENAME TO PlayTimeItem;
        CREATE INDEX IF NOT EXISTS IX_PlayTimeItem_GameBiz ON PlayTimeItem(GameBiz);
        CREATE INDEX IF NOT EXISTS IX_PlayTimeItem_Pid ON PlayTimeItem(Pid);
        CREATE INDEX IF NOT EXISTS IX_PlayTimeItem_State ON PlayTimeItem(State);

        DROP TABLE IF EXISTS GachaLogUrl;
        CREATE TABLE IF NOT EXISTS GachaLogUrl
        (
            GameBiz TEXT    NOT NULL,
            Uid     INTEGER NOT NULL,
            Url     TEXT    NOT NULL,
            Time    TEXT    NOT NULL,
            PRIMARY KEY (GameBiz, Uid)
        );

        ALTER TABLE GameRecordRole ADD COLUMN HeadIcon TEXT;

        PRAGMA USER_VERSION = 12;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v13 = """
        BEGIN TRANSACTION;

        DROP TABLE IF EXISTS GameAccount;
        CREATE TABLE IF NOT EXISTS GameAccount
        (
            SHA256  TEXT    NOT NULL,
            GameBiz INTEGER NOT NULL,
            Uid     TEXT    NOT NULL,
            Name    TEXT    NOT NULL,
            Value   BLOB    NOT NULL,
            Time    TEXT    NOT NULL,
            PRIMARY KEY (SHA256, GameBiz)
        );
        CREATE INDEX IF NOT EXISTS IX_GameAccount_GameBiz ON GameAccount (GameBiz);

        CREATE TABLE IF NOT EXISTS ZZZGachaInfo
        (
            Id          INTEGER NOT NULL PRIMARY KEY,
            Name        TEXT,
            Icon        TEXT,
            Rarity      INTEGER NOT NULL,
            ElementType INTEGER NOT NULL,
            Profession  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_ZZZGachaInfo_Name ON ZZZGachaInfo (Name);

        PRAGMA USER_VERSION = 13;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v14 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS ShiyuDefenseInfo
        (
            Uid            INTEGER NOT NULL,
            ScheduleId     INTEGER NOT NULL,
            BeginTime      TEXT    NOT NULL,
            EndTime        TEXT    NOT NULL,
            HasData        INTEGER NOT NULL,
            MaxRating      Text,
            MaxRatingTimes INTEGER NOT NULL,
            MaxLayer       INTEGER NOT NULL,
            Value          TEXT,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_ShiyuDefenseInfo_ScheduleId ON ShiyuDefenseInfo (ScheduleId);

        CREATE TABLE IF NOT EXISTS DeadlyAssaultInfo
        (
            Uid            INTEGER NOT NULL,
            ZoneId         INTEGER NOT NULL,
            StartTime      TEXT    NOT NULL,
            EndTime        TEXT    NOT NULL,
            HasData        INTEGER NOT NULL,
            RankPercent    INTEGER NOT NULL,
            TotalScore     INTEGER NOT NULL,
            TotalStar      INTEGER NOT NULL,
            Value          TEXT,
            PRIMARY KEY (Uid, ZoneId)
        );
        CREATE INDEX IF NOT EXISTS IX_DeadlyAssaultInfo_ZoneId ON DeadlyAssaultInfo (ZoneId);

        PRAGMA USER_VERSION = 14;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v15 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS StygianOnslaughtInfo
        (
            Uid           INTEGER NOT NULL,
            ScheduleId    INTEGER NOT NULL,
            StartDateTime TEXT    NOT NULL,
            EndDateTime   TEXT    NOT NULL,
            Difficulty    INTEGER NOT NULL,
            Second        INTEGER NOT NULL,
            Value         TEXT,
            PRIMARY KEY (Uid, ScheduleId)
        );
        CREATE INDEX IF NOT EXISTS IX_StygianOnslaughtInfo_ScheduleId ON StygianOnslaughtInfo (ScheduleId);

        PRAGMA USER_VERSION = 15;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v16 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS GenshinBeyondGachaItem
        (
            Uid         INTEGER,
            Id          INTEGER,
            Region      TEXT,
            OpGachaType INTEGER,
            ScheduleId  INTEGER,
            ItemType    TEXT,
            ItemId      INTEGER,
            ItemName    TEXT,
            RankType    INTEGER,
            IsUp        INTEGER,
            Time        TEXT,
            PRIMARY KEY (Uid, Id)
        );
        CREATE INDEX IF NOT EXISTS IX_GenshinBeyondGachaItem_Id ON GenshinBeyondGachaItem (Id);
        CREATE INDEX IF NOT EXISTS IX_GenshinBeyondGachaItem_OpGachaType ON GenshinBeyondGachaItem (OpGachaType);

        ALTER TABLE ImaginariumTheaterInfo RENAME TO GenshinImaginariumTheaterInfo;
        ALTER TABLE SpiralAbyssInfo RENAME TO GenshinSpiralAbyssInfo;
        ALTER TABLE StygianOnslaughtInfo RENAME TO GenshinStygianOnslaughtInfo;
        ALTER TABLE TravelersDiaryAwardItem RENAME TO GenshinTravelersDiaryAwardItem;
        ALTER TABLE TravelersDiaryMonthData RENAME TO GenshinTravelersDiaryMonthData;

        ALTER TABLE ApocalypticShadowInfo RENAME TO StarRailApocalypticShadowInfo;
        ALTER TABLE ForgottenHallInfo RENAME TO StarRailForgottenHallInfo;
        ALTER TABLE PureFictionInfo RENAME TO StarRailPureFictionInfo;
        ALTER TABLE SimulatedUniverseRecord RENAME TO StarRailSimulatedUniverseRecord;
        ALTER TABLE TrailblazeCalendarMonthData RENAME TO StarRailTrailblazeCalendarMonthData;
        ALTER TABLE TrailblazeCalendarDetailItem RENAME TO StarRailTrailblazeCalendarDetailItem;

        ALTER TABLE DeadlyAssaultInfo RENAME TO ZZZDeadlyAssaultInfo;
        ALTER TABLE InterKnotReportSummary RENAME TO ZZZInterKnotReportSummary;
        ALTER TABLE InterKnotReportDetailItem RENAME TO ZZZInterKnotReportDetailItem;
        ALTER TABLE ShiyuDefenseInfo RENAME TO ZZZShiyuDefenseInfo;

        PRAGMA USER_VERSION = 16;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v17 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS GenshinBeyondGachaInfo
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT,
            Rank INTEGER NOT NULL,
            Icon TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_GenshinBeyondGachaInfo_Name ON GenshinBeyondGachaInfo (Name);

        PRAGMA USER_VERSION = 17;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v18 = """
        BEGIN TRANSACTION;

        CREATE TABLE GenshinQueryItem_dg_tmp
        (
            Uid      INTEGER NOT NULL,
            Id       INTEGER NOT NULL,
            AddNum   INTEGER NOT NULL,
            Reason   TEXT,
            DateTime TEXT,
            Type     INTEGER NOT NULL,
            Icon     TEXT,
            Level    INTEGER NOT NULL,
            Quality  INTEGER NOT NULL,
            Name     TEXT,
            PRIMARY KEY (Uid, Id, Type)
        );
        INSERT INTO GenshinQueryItem_dg_tmp(Uid, Id, AddNum, Reason, DateTime, Type, Icon, Level, Quality, Name)
        SELECT Uid, Id, AddNum, Reason, DateTime, Type, Icon, Level, Quality, Name FROM GenshinQueryItem;
        DROP TABLE GenshinQueryItem;
        ALTER TABLE GenshinQueryItem_dg_tmp RENAME TO GenshinQueryItem;
        CREATE INDEX IX_GenshinQueryItem_AddNum ON GenshinQueryItem (AddNum);
        CREATE INDEX IX_GenshinQueryItem_DateTime ON GenshinQueryItem (DateTime);
        CREATE INDEX IX_GenshinQueryItem_Id ON GenshinQueryItem (Id);
        CREATE INDEX IX_GenshinQueryItem_Reason ON GenshinQueryItem (Reason);
        CREATE INDEX IX_GenshinQueryItem_Type ON GenshinQueryItem (Type);

        CREATE TABLE ZZZShiyuDefenseInfo_dg_tmp
        (
            Uid            INTEGER           NOT NULL,
            ScheduleId     INTEGER           NOT NULL,
            BeginTime      TEXT              NOT NULL,
            EndTime        INTEGER           NOT NULL,
            Version        TEXT,
            HasData        INTEGER           NOT NULL,
            MaxRating      TEXT,
            MaxRatingTimes INTEGER DEFAULT 0 NOT NULL,
            MaxLayer       INTEGER DEFAULT 0 NOT NULL,
            V2Score        INTEGER DEFAULT 0 NOT NULL,
            Value          TEXT,
            PRIMARY KEY (Uid, ScheduleId)
        );
        INSERT INTO ZZZShiyuDefenseInfo_dg_tmp(Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, MaxRatingTimes, MaxLayer, Value)
        SELECT Uid, ScheduleId, BeginTime, EndTime, 'v1', HasData, MaxRating, MaxRatingTimes, MaxLayer, Value FROM ZZZShiyuDefenseInfo;
        DROP TABLE ZZZShiyuDefenseInfo;
        ALTER TABLE ZZZShiyuDefenseInfo_dg_tmp RENAME TO ZZZShiyuDefenseInfo;

        CREATE TABLE IF NOT EXISTS StarRailChallengePeakData
        (
            Uid         INTEGER NOT NULL,
            GroupId     INTEGER NOT NULL,
            GameVersion TEXT    NOT NULL,
            BossStars   INTEGER NOT NULL DEFAULT 0,
            MobStars    INTEGER NOT NULL DEFAULT 0,
            BossIcon    TEXT,
            Value       TEXT,
            PRIMARY KEY (Uid, GroupId)
        );

        PRAGMA USER_VERSION = 18;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v19 = """
        BEGIN TRANSACTION;

        ALTER TABLE StarRailApocalypticShadowInfo ADD COLUMN ExtraStarNum INTEGER DEFAULT 0 NOT NULL;
        ALTER TABLE StarRailApocalypticShadowInfo ADD COLUMN TierceBossIcon TEXT;
        ALTER TABLE StarRailPureFictionInfo ADD COLUMN ExtraStarNum INTEGER DEFAULT 0 NOT NULL;
        ALTER TABLE StarRailForgottenHallInfo ADD COLUMN ExtraStarNum INTEGER DEFAULT 0 NOT NULL;

        PRAGMA USER_VERSION = 19;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v20 = """
        BEGIN TRANSACTION;

        ALTER TABLE ZZZDeadlyAssaultInfo ADD COLUMN HasHard INTEGER DEFAULT 0 NOT NULL;
        ALTER TABLE ZZZDeadlyAssaultInfo ADD COLUMN HardTotalScore INTEGER DEFAULT 0 NOT NULL;
        ALTER TABLE ZZZDeadlyAssaultInfo ADD COLUMN HardTotalStar INTEGER DEFAULT 0 NOT NULL;

        PRAGMA USER_VERSION = 20;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v21 = """
        BEGIN TRANSACTION;

        UPDATE PlayTimeItem SET GameBiz = REPLACE(GameBiz, '_bilibili', '_cn') WHERE GameBiz LIKE '%_bilibili';

        CREATE TABLE IF NOT EXISTS PlayTimeStats
        (
            Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            GameBiz      TEXT    NOT NULL,
            Pid          INTEGER NOT NULL,
            StartTime    INTEGER NOT NULL,
            EndTime      INTEGER NOT NULL,
            Interruption INTEGER NOT NULL DEFAULT 0,
            Type         INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_PlayTimeStats_GameBiz_StartTime_Pid ON PlayTimeStats (GameBiz, StartTime, Pid);

        PRAGMA USER_VERSION = 21;
        COMMIT TRANSACTION;
        """;

    private const string Sql_v22 = """
        BEGIN TRANSACTION;

        DROP TABLE IF EXISTS Setting_dg_tmp;
        CREATE TABLE Setting_dg_tmp
        (
            Key    TEXT NOT NULL,
            GameId TEXT NOT NULL DEFAULT '',
            Value  TEXT NULL,
            PRIMARY KEY (Key, GameId)
        );

        CREATE TEMP TABLE GameBizSplit_tmp
        (
            Suffix TEXT NOT NULL PRIMARY KEY,
            GameId TEXT NOT NULL
        );
        INSERT INTO GameBizSplit_tmp (Suffix, GameId) VALUES
            ('_hk4e_cn',       'hk4e_cn-1Z8W5NHUQb'),
            ('_hk4e_global',   'hk4e_global-gopR6Cufr3'),
            ('_hk4e_bilibili', 'hk4e_bilibili-T2S0Gz4Dr2'),
            ('_hkrpg_cn',      'hkrpg_cn-64kMb5iAWu'),
            ('_hkrpg_global',  'hkrpg_global-4ziysqXOQ8'),
            ('_hkrpg_bilibili','hkrpg_bilibili-EdtUqXfCHh'),
            ('_nap_cn',        'nap_cn-x6znKlJ0xK'),
            ('_nap_global',    'nap_global-U5hbdsT9W7'),
            ('_nap_bilibili',  'nap_bilibili-HXAFlmYa17'),
            ('_bh3_cn',        'bh3_cn-osvnlOc0S8'),
            ('_bh3_global',    'bh3_global-5TIVvvcwtM'),
            ('_bh3_jp',        'bh3_global-g0mMIvshDb'),
            ('_bh3_kr',        'bh3_global-uxB4MC7nzC'),
            ('_bh3_os',        'bh3_global-bxPTXSET5t'),
            ('_bh3_asia',      'bh3_global-wkE5P5WsIf'),
            ('_bh3_overseas',  'bh3_global-bxPTXSET5t'),
            ('_bh3_tw',        'bh3_global-wkE5P5WsIf'),
            ('_hyg_cn',        'hyg_cn-ERYDdvf0w8'),
            ('_hyg_global',    'hyg_global-viiywh5G2H'),
            ('_abc_cn',        'abc_cn-m8TGERqknS'),
            ('_abc_global',    'abc_global-yneO4yGc4x'),
            ('_hk4e',          'hk4e'),
            ('_hkrpg',         'hkrpg'),
            ('_nap',           'nap'),
            ('_bh3',           'bh3');

        CREATE TEMP TABLE KeyRename_tmp
        (
            Old TEXT NOT NULL PRIMARY KEY,
            New TEXT NOT NULL
        );
        INSERT INTO KeyRename_tmp (Old, New) VALUES
            ('bg',                           'Bg'),
            ('custom_bg',                    'CustomBg'),
            ('enable_third_party_tool',      'EnableThirdPartyTool'),
            ('third_party_tool_path',        'ThirdPartyToolPath'),
            ('start_argument',               'StartArgument'),
            ('external_screenshot_folder',   'ExternalScreenshotFolder'),
            ('enable_dx12',                  'EnableDX12'),
            ('last_gacha_uid',               'LastGachaUid'),
            ('display_gacha_banners',        'DisplayGachaBanners'),
            -- 本 fork（main）的安装路径设置：旧键 install_path_{GameBiz} / install_path_removable_{GameBiz}
            -- 按 GameId 拆分成 0.19 的 InstallPath / InstallPathRemovable，v24 再搬进 GameEntry 表。
            ('install_path',                 'InstallPath'),
            ('install_path_removable',       'InstallPathRemovable');

        INSERT OR REPLACE INTO Setting_dg_tmp (Key, GameId, Value)
        SELECT CASE
            WHEN sp.Suffix IS NULL THEN s.Key ELSE substr(s.Key, 1, length(s.Key) - length(sp.Suffix)) END,
            IFNULL(sp.GameId, ''),
            s.Value
        FROM Setting AS s
        LEFT JOIN GameBizSplit_tmp AS sp
               ON substr(s.Key, -length(sp.Suffix)) = sp.Suffix
              AND substr(s.Key, 1, length(s.Key) - length(sp.Suffix)) IN (SELECT Old FROM KeyRename_tmp)
        WHERE sp.Suffix IS NOT NULL
           OR s.Key IN (SELECT Old FROM KeyRename_tmp);

        UPDATE Setting_dg_tmp
        SET Key = (SELECT New FROM KeyRename_tmp WHERE Old = Key)
        WHERE Key IN (SELECT Old FROM KeyRename_tmp);

        -- 上游原本只保留大写开头的键（Key GLOB '[A-Z]*'），会把 main 分支的小写设置键
        -- （install_path_*、launch_profiles_*、daily_note_notify_* 等）直接丢掉。
        -- 本 fork 保留所有没有被上面重命名掉的旧键，统一写到 GameId = '' 的行里，
        -- 保证 main 已有功能升级后设置不丢；真正需要换表的键由 v24 再迁移。
        INSERT OR REPLACE INTO Setting_dg_tmp (Key, GameId, Value)
        SELECT s.Key, '', s.Value
        FROM Setting AS s
        WHERE s.Key NOT IN (SELECT Old FROM KeyRename_tmp)
          AND NOT EXISTS (SELECT 1
                          FROM GameBizSplit_tmp AS sp
                          WHERE substr(s.Key, -length(sp.Suffix)) = sp.Suffix
                            AND substr(s.Key, 1, length(s.Key) - length(sp.Suffix)) IN (SELECT Old FROM KeyRename_tmp));

        DROP TABLE GameBizSplit_tmp;
        DROP TABLE KeyRename_tmp;
        DROP TABLE IF EXISTS Setting;
        ALTER TABLE Setting_dg_tmp RENAME TO Setting;

        PRAGMA USER_VERSION = 22;
        COMMIT TRANSACTION;
        """;
    private const string Sql_v23 = """
        BEGIN TRANSACTION;

        CREATE TABLE IF NOT EXISTS CacheEntry
        (
            Key        TEXT    NOT NULL PRIMARY KEY,
            Value      BLOB    NULL,
            ExpireTime INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_CacheEntry_ExpireTime ON CacheEntry (ExpireTime);

        CREATE TABLE IF NOT EXISTS GameEntry
        (
            GameId      TEXT    NOT NULL PRIMARY KEY,
            GameBiz     TEXT    NOT NULL,
            LauncherId  TEXT    NOT NULL,
            InstallPath TEXT    NULL,
            Removable   INTEGER NOT NULL,
            IsPinned    INTEGER NOT NULL,
            PinnedOrder INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_GameEntry_LauncherId ON GameEntry (LauncherId);
        CREATE INDEX IF NOT EXISTS IX_GameEntry_IsPinned ON GameEntry (IsPinned);

        CREATE TABLE IF NOT EXISTS PassportUserInfo
        (
            Region     INTEGER NOT NULL,
            Aid        INTEGER NOT NULL,
            Mid        TEXT    NOT NULL,
            Stoken     BLOB    NOT NULL,
            SecretType INTEGER NOT NULL,
            Nickname   TEXT    NULL,
            AvatarUrl  TEXT    NULL,
            Pendant    TEXT    NULL,
            PRIMARY KEY (Region, Aid)
        );

        PRAGMA USER_VERSION = 23;
        COMMIT TRANSACTION;
        """;

    #endregion



}
