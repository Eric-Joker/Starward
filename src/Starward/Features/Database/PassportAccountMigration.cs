using Microsoft.Data.Sqlite;
using Starward.Core;
using Starward.Core.Passport;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Starward.Features.Database;

/// <summary>
/// 通行证账号存储的收口迁移（v24 的一部分）。
/// <para>
/// 本分支原来自己有一张账号池表 <c>PassportAccount</c>，里面存着每个账号的整包令牌
/// （SToken + ltoken + cookie_token + 登录方式）。上游 0.19 的 <c>PassportUserInfo</c> 表
/// 也要存 SToken，于是一枚 SToken 有了两个 owner：服务端换票后两边一先一后地覆盖，
/// 就会出现「米游社功能都正常，账号中心却说登录已过期」。
/// </para>
/// <para>
/// 现在只保留一个 owner：SToken 只写 <c>PassportUserInfo</c>（上游的表，MainView 的账号浮窗、
/// 自动登录、战绩都读它）；派生令牌和上游一样只存在工具箱 Cookie 里，不另存一份。
/// 这个迁移负责把老账号池里的账号补进上游表，然后删掉老表。
/// </para>
/// </summary>
internal static class PassportAccountMigration
{

    /// <summary>老账号池表名。迁移完成后这张表会被删掉。</summary>
    private const string LegacyTable = "PassportAccount";


    /// <summary>「当前会话」的指针设置键：值是 <c>miHoYo:81107513</c> 这种「区域:账号 ID」。</summary>
    public const string CurrentAccountSettingKey = "CurrentPassportAccount";


    /// <summary>老版本把整包令牌（含当前账号）存在这个设置里，收口后只需要一个指针。</summary>
    private const string LegacySessionSettingKey = "PassportTokenJson";


    /// <summary>老账号池自己的拖动顺序键，已经被 <c>PassportUserOrder</c> 取代。</summary>
    private const string LegacyOrderSettingKey = "PassportAccountOrder";



    /// <summary>
    /// 执行收口迁移。可以在已经有数据的库上重复执行：上游表已有行就保持原样，老表不存在就跳过。
    /// </summary>
    public static void Migrate(SqliteConnection con, Func<string, byte[]> encryptStoken)
    {
        if (TableExists(con, LegacyTable))
        {
            try
            {
                bool migrated = MigrateLegacyAccounts(con, encryptStoken);
                // 只有确认每个账号都已经落到上游表里才敢删老表：上游表还不存在（或者有行解不开）时
                // 留着老表，下次启动再试一次，绝不主动丢账号。
                if (migrated && TableExists(con, "PassportUserInfo"))
                {
                    Execute(con, $"DROP TABLE {LegacyTable};");
                }
                else
                {
                    Debug.WriteLine("The legacy passport account table is kept because not every account could be migrated.");
                }
            }
            catch (Exception ex)
            {
                // 迁移失败也不能把账号表带走：至少让升级继续，用户还能在账号中心里重新登录。
                Debug.WriteLine($"Migrate the legacy passport accounts failed: {ex}");
            }
        }
        try
        {
            MigrateLegacySession(con);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Migrate the legacy passport session failed: {ex}");
        }
    }



    /// <summary>
    /// 把老账号池的每一行搬到上游表。
    /// </summary>
    /// <returns>每个账号都有 SToken 落到上游表里时返回 true（可以安全删掉老表）。</returns>
    private static bool MigrateLegacyAccounts(SqliteConnection con, Func<string, byte[]> encryptStoken)
    {
        bool allMigrated = true;
        foreach (LegacyAccount row in ReadLegacyAccounts(con))
        {
            PassportTokenSet? tokenSet = TryReadTokenSet(row.Token);
            try
            {
                // 上游表里的 SToken 才是 owner：已经有这一行就保持原样，不要用老副本覆盖。
                if (tokenSet?.HasStoken is true)
                {
                    if (!HasPassportUserInfo(con, row.Region, row.Aid))
                    {
                        InsertPassportUserInfo(con, row, tokenSet, encryptStoken);
                    }
                }
                else if (!HasPassportUserInfo(con, row.Region, row.Aid))
                {
                    // 令牌解不开、上游表里又没有这个账号：账号会从列表里消失，老表必须留着。
                    Debug.WriteLine($"The legacy passport account {row.Region}:{row.Aid} has no usable stoken.");
                    allMigrated = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Migrate the legacy passport account {row.Region}:{row.Aid} failed: {ex}");
                allMigrated = false;
            }
        }
        return allMigrated;
    }



    /// <summary>
    /// 老版本的「当前账号」是整包存在 <see cref="LegacySessionSettingKey"/> 里的，
    /// 收口后只剩一个指向账号的指针，这里把指针读出来、把整包令牌删掉；
    /// 账号顺序也只保留一套（<c>PassportUserOrder</c>，账号中心与账号池共用）。
    /// </summary>
    private static void MigrateLegacySession(SqliteConnection con)
    {
        string? stored = QueryFirstOrDefault<string>(con,
            "SELECT Value FROM Setting WHERE Key = @key AND GameId = '' LIMIT 1;",
            ("@key", LegacySessionSettingKey));
        if (!string.IsNullOrWhiteSpace(stored))
        {
            PassportTokenSet? tokenSet = TryReadTokenSet(Encoding.UTF8.GetBytes(stored));
            if (tokenSet is not null)
            {
                if (tokenSet.HasStoken && ParsePointer(GetPointer(tokenSet)) is { } pointer)
                {
                    Execute(con, """
                        INSERT OR IGNORE INTO Setting (Key, GameId, Value) VALUES (@key, '', @value);
                        """, ("@key", CurrentAccountSettingKey), ("@value", GetPointer(tokenSet)));
                }
                Execute(con, "DELETE FROM Setting WHERE Key = @key;", ("@key", LegacySessionSettingKey));
            }
            // 解不开就留着：下次启动再试一次，别把用户的当前账号直接弄丢。
        }
        // 账号池那套顺序（值是 mid:xxx）已经并入 PassportUserOrder（值是账号 ID）。
        Execute(con, "DELETE FROM Setting WHERE Key = @key;", ("@key", LegacyOrderSettingKey));
    }



    /// <summary>账号指针：<c>miHoYo:81107513</c>。区域用枚举名，跨版本读得懂。</summary>
    public static string GetPointer(PassportTokenSet tokenSet)
    {
        return $"{tokenSet.Region}:{tokenSet.Aid}";
    }



    /// <summary>解析账号指针，格式不认识时返回 null。</summary>
    public static (ServerRegion Region, long Aid)? ParsePointer(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
        {
            return null;
        }
        int index = pointer.IndexOf(':');
        if (index <= 0)
        {
            return null;
        }
        if (!Enum.TryParse(pointer[..index], ignoreCase: true, out ServerRegion region))
        {
            return null;
        }
        return long.TryParse(pointer[(index + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long aid) && aid > 0
            ? (region, aid)
            : null;
    }




    private static void InsertPassportUserInfo(SqliteConnection con, LegacyAccount row, PassportTokenSet tokenSet, Func<string, byte[]> encryptStoken)
    {
        Execute(con, """
            INSERT OR REPLACE INTO PassportUserInfo (Region, Aid, Mid, Stoken, SecretType, Nickname, AvatarUrl, Pendant)
            VALUES (@Region, @Aid, @Mid, @Stoken, 0, @Nickname, NULL, NULL);
            """,
            ("@Region", (int)row.Region), ("@Aid", row.Aid),
            ("@Mid", tokenSet.Mid ?? row.Mid ?? ""),
            ("@Stoken", encryptStoken(tokenSet.Stoken!)),
            ("@Nickname", tokenSet.Nickname ?? row.Nickname));
    }



    /// <summary>老账号池表里的一行（迁移用，只保留上游表需要的字段）。</summary>
    private sealed class LegacyAccount
    {
        public ServerRegion Region { get; set; }
        public long Aid { get; set; }
        public string? Mid { get; set; }
        public string? Nickname { get; set; }
        public byte[] Token { get; set; } = [];
    }



    private static List<LegacyAccount> ReadLegacyAccounts(SqliteConnection con)
    {
        // 老 main 的表没有 Region 列（只有国服），迁移时按国服处理。
        bool hasRegion = HasColumn(con, LegacyTable, "Region");
        var list = new List<LegacyAccount>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            SELECT {(hasRegion ? "Region" : "1")} AS Region, Aid, Mid, Nickname, Token
            FROM {LegacyTable};
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LegacyAccount
            {
                Region = (ServerRegion)reader.GetInt32(0),
                Aid = reader.GetInt64(1),
                Mid = reader.IsDBNull(2) ? null : reader.GetString(2),
                Nickname = reader.IsDBNull(3) ? null : reader.GetString(3),
                Token = reader.IsDBNull(4) ? [] : (byte[])reader.GetValue(4),
            });
        }
        return list;
    }



    /// <summary>
    /// 解开老账号池里那包令牌（DPAPI 保护的 base64，外面又套了一层 UTF-8）。
    /// 解不开时返回 null：账号本身的信息仍然会迁移，只是没有派生令牌。
    /// </summary>
    private static PassportTokenSet? TryReadTokenSet(byte[] token)
    {
        if (token.Length == 0)
        {
            return null;
        }
        try
        {
            string text = Encoding.UTF8.GetString(token);
            string? json = DpapiHelper.Unprotect(text);
            if (string.IsNullOrWhiteSpace(json) && text.TrimStart().StartsWith('{'))
            {
                // 更老的版本直接写明文 JSON。
                json = text;
            }
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PassportTokenSet>(json, PassportJson.Options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unprotect the legacy passport token failed: {ex}");
            return null;
        }
    }



    private static bool TableExists(SqliteConnection con, string table)
    {
        return QueryFirstOrDefault<long>(con, "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @name;", ("@name", table)) > 0;
    }



    private static bool HasColumn(SqliteConnection con, string table, string column)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(1) FROM pragma_table_info('{table}') WHERE name = @name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@name", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }



    private static bool HasPassportUserInfo(SqliteConnection con, ServerRegion region, long aid)
    {
        if (!TableExists(con, "PassportUserInfo"))
        {
            return false;
        }
        return QueryFirstOrDefault<long>(con, "SELECT COUNT(1) FROM PassportUserInfo WHERE Region = @Region AND Aid = @Aid;",
                                         ("@Region", (int)region), ("@Aid", aid)) > 0;
    }



    private static void Execute(SqliteConnection con, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }



    private static T? QueryFirstOrDefault<T>(SqliteConnection con, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }


}
