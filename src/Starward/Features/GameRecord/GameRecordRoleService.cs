using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.Passport;
using Starward.Features.Database;
using Starward.Features.GameRecord.BH3;
using Starward.Features.GameRecord.Genshin;
using Starward.Features.GameRecord.StarRail;
using Starward.Features.GameRecord.ZZZ;
using Starward.Features.Passport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord;

internal class GameRecordRoleService : GameRecordServiceBase
{

    private readonly PassportService _passportService;

    private readonly GameRecordBH3Service _bh3Service;

    private readonly GameRecordGenshinService _genshinService;

    private readonly GameRecordStarRailService _starRailService;

    private readonly GameRecordZZZService _zzzService;



    public GameRecordRoleService(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _passportService = serviceProvider.GetRequiredService<PassportService>();
        _bh3Service = serviceProvider.GetRequiredService<GameRecordBH3Service>();
        _genshinService = serviceProvider.GetRequiredService<GameRecordGenshinService>();
        _starRailService = serviceProvider.GetRequiredService<GameRecordStarRailService>();
        _zzzService = serviceProvider.GetRequiredService<GameRecordZZZService>();
    }



    /// <summary>
    /// 获取游戏账号
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<List<GameRecordRole>> GetGameRolesAsync(GameBiz gameBiz, string cookie, CancellationToken cancellationToken = default)
    {
        return gameBiz.Game switch
        {
            GameBiz.bh3 => _bh3Service.GetGameRolesAsync(gameBiz, cookie, cancellationToken),
            GameBiz.hk4e => _genshinService.GetGameRolesAsync(gameBiz, cookie, cancellationToken),
            GameBiz.hkrpg => _starRailService.GetGameRolesAsync(gameBiz, cookie, cancellationToken),
            GameBiz.nap => _zzzService.GetGameRolesAsync(gameBiz, cookie, cancellationToken),
            _ => throw new NotSupportedException($"GameBiz '{gameBiz}' is not supported."),
        };
    }


    /// <summary>
    /// 更新单个游戏角色，失败时通过通行证刷新cookie
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task UpdateGameRoleAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await GetGameRolesAsync(role.GameBiz, role.Cookie!, cancellationToken).ConfigureAwait(false);
            AddGameRoles(list);
        }
        catch (miHoYoApiException ex) when (ex.ReturnCode is -100 or 10001)
        {
            await UpdateGameRoleInfoAsync(role, cancellationToken).ConfigureAwait(false);
        }
    }



    private async Task UpdateGameRoleInfoAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        var passport = PassportService.GetPassportUserInfo(role.Aid);
        if (passport is null || passport.ErrorType is not PassportErrorType.Success || string.IsNullOrWhiteSpace(passport.Stoken))
        {
            return;
        }
        var roles = await UpdateGameRolesByPassportAsync(passport, cancellationToken);
        role = roles.FirstOrDefault(x => x.Uid == role.Uid)!;
        if (role is not null)
        {
            var list = await GetGameRolesAsync(role.GameBiz, role.Cookie!, cancellationToken).ConfigureAwait(false);
            AddGameRoles(list);
        }
    }



    public async Task<List<GameRecordRole>> UpdateGameRolesByPassportAsync(PassportUserInfo passport, CancellationToken cancellation = default)
    {
        var roles = await _passportService.UpdateGameRolesAsync(passport, await GetDeviceInfoAsync(passport.Region, cancellation), cancellation).ConfigureAwait(false);
        AddGameRoles(roles);
        return roles;
    }



    /// <summary>
    /// 拉取角色所属游戏的实时便笺。按角色自身的区服选客户端与设备指纹，
    /// 后台便笺提醒与提醒设置对话框用它，不必为了发请求去改别的共享状态。
    /// </summary>
    /// <param name="role">战绩角色。</param>
    /// <param name="forceUpdate">为 false 时 5 分钟内复用缓存。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该角色所属游戏的便笺对象。</returns>
    /// <exception cref="NotSupportedException">角色所属游戏没有实时便笺。</exception>
    public async Task<object> GetDailyNoteByRoleAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        GameBiz biz = role.GameBiz;
        return biz.Game switch
        {
            GameBiz.hk4e => await _genshinService.GetGenshinDailyNoteAsync(role, forceUpdate, cancellationToken).ConfigureAwait(false),
            GameBiz.hkrpg => await _starRailService.GetStarRailDailyNoteAsync(role, forceUpdate, cancellationToken).ConfigureAwait(false),
            GameBiz.nap => await _zzzService.GetZZZDailyNoteAsync(role, forceUpdate, cancellationToken).ConfigureAwait(false),
            GameBiz.bh3 => await _bh3Service.GetBH3DailyNoteAsync(role, forceUpdate, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Daily note is not supported for {role.GameBiz}."),
        };
    }



    /// <summary>
    /// 数据库中全部游戏角色（跨所有账号 cookie 与游戏），按账号(cookie)再按游戏排序。
    /// 没有 Cookie 的角色跳过（它拉不了便笺）。后台的便笺提醒与提醒设置对话框用它。
    /// </summary>
    /// <returns>可参与便笺提醒的角色列表。</returns>
    public static List<GameRecordRole> GetAllGameRoles()
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole ORDER BY Cookie, GameBiz;");
        return list.Where(x => !string.IsNullOrWhiteSpace(x.Cookie)).ToList();
    }



    /// <summary>
    /// 保存游戏角色
    /// </summary>
    /// <param name="roles"></param>
    public static void AddGameRoles(IEnumerable<GameRecordRole> roles)
    {
        var list = roles as IList<GameRecordRole> ?? roles.ToList();
        if (list.Count == 0)
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        dapper.Execute("""
            INSERT INTO GameRecordRole (Uid, GameBiz, Nickname, Level, Region, RegionName, Cookie, HeadIcon, Aid)
            VALUES (@Uid, @GameBiz, @Nickname, @Level, @Region, @RegionName, @Cookie, @HeadIcon, @Aid)
            ON CONFLICT(Uid, GameBiz) DO UPDATE SET Nickname = excluded.Nickname,
                                                    Level = excluded.Level,
                                                    Region = excluded.Region,
                                                    RegionName = excluded.RegionName,
                                                    Cookie = COALESCE(excluded.Cookie, GameRecordRole.Cookie),
                                                    HeadIcon = COALESCE(excluded.HeadIcon, GameRecordRole.HeadIcon),
                                                    Aid = COALESCE(NULLIF(excluded.Aid, 0), GameRecordRole.Aid);
            """, list, t);
        t.Commit();
    }



    public static List<GameRecordRole> GetGameRoles(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz;", new { gameBiz });
        return list.ToList();
    }



    public static GameRecordRole? GetLastSelectGameRecordRoleOrTheFirstOne(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        var role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key AND s.GameId = @gameId LIMIT 1;
            """, new { gameBiz, key = "LastSelectGameRecordRole", gameId = gameBiz.Value });
        return role ??= dapper.QueryFirstOrDefault<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz LIMIT 1;", new { gameBiz });
    }



    /// <summary>
    /// 记录某个游戏「最近选择的角色」，供每日便笺、签到、公告等按角色解析的模块使用。
    /// 与 <see cref="GetLastSelectGameRecordRoleOrTheFirstOne"/> 使用同一个设置键。
    /// </summary>
    /// <param name="gameBiz">游戏区服。</param>
    /// <param name="role">要记住的角色。</param>
    public static void SetLastSelectGameRecordRole(GameBiz gameBiz, GameRecordRole role)
    {
        if (string.IsNullOrWhiteSpace(gameBiz.Value) || role is null || role.Uid <= 0)
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, GameId, Value) VALUES (@key, @gameId, @value);",
                       new { key = "LastSelectGameRecordRole", gameId = gameBiz.Value, value = role.Uid.ToString() });
    }




    #region 通行证账号池共用的工具箱账号 / 角色读写

    // 主分支（main）的通行证账号池把工具箱账号信息（GameRecordUser）与角色信息（GameRecordRole）
    // 当作账号池的延伸：登录、刷新令牌、切换账号都要把工具箱里的 Cookie 换成新凭据。
    // 上游 0.19 把这两个表交给 PassportUserInfo 按需派生，这里只补账号池需要的那几个读写方法，
    // 不引入主分支整条 GameRecordService。


    /// <summary>参与通行证账号池同步的国服游戏区服。</summary>
    private static readonly GameBiz[] ChinaGameBizs = [GameBiz.bh3_cn, GameBiz.hk4e_cn, GameBiz.hkrpg_cn, GameBiz.nap_cn];


    /// <summary>
    /// 按 UID 读取工具箱账号（<c>GameRecordUser</c>）。账号池同步只关心国服（<paramref name="isHoyolab"/> = false）。
    /// </summary>
    public static GameRecordUser? GetRecordUser(long uid, bool isHoyolab)
    {
        if (uid <= 0)
        {
            return null;
        }
        using var dapper = DatabaseService.CreateConnection();
        return dapper.QueryFirstOrDefault<GameRecordUser>("SELECT * FROM GameRecordUser WHERE Uid = @uid AND IsHoyolab = @isHoyolab LIMIT 1;", new { uid, isHoyolab });
    }


    /// <summary>写入（新增或更新）一条工具箱账号。</summary>
    public static void SaveRecordUser(GameRecordUser user)
    {
        if (user is null || user.Uid <= 0)
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GameRecordUser (Uid, IsHoyolab, Nickname, Avatar, Introduce, Gender, AvatarUrl, Pendant, Cookie)
            VALUES (@Uid, @IsHoyolab, @Nickname, @Avatar, @Introduce, @Gender, @AvatarUrl, @Pendant, @Cookie);
            """, user);
    }


    /// <summary>
    /// 只更新账号与角色的 Cookie，不请求任何接口。用于通行证令牌刷新后同步工具箱的鉴权数据。
    /// <para>
    /// 一个米游社账号在工具箱里可能对应多个游戏角色（不同游戏，甚至同一游戏的多个账号），
    /// 它们的 Cookie 是同一份鉴权信息，必须一起换掉，否则刷新令牌后工具箱里会留着旧的 Cookie。
    /// 角色表里的 Uid 是游戏内 UID 而不是米游社 ID，所以这里按 Cookie 里的 account_id 对应。
    /// </para>
    /// </summary>
    public static void UpdateRecordUserCookie(long uid, bool isHoyolab, string cookie)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        string? oldCookie = dapper.QueryFirstOrDefault<string>("SELECT Cookie FROM GameRecordUser WHERE Uid = @uid AND IsHoyolab = @isHoyolab LIMIT 1;", new { uid, isHoyolab });
        using var t = dapper.BeginTransaction();
        dapper.Execute("UPDATE GameRecordUser SET Cookie = @cookie WHERE Uid = @uid AND IsHoyolab = @isHoyolab;", new { uid, isHoyolab, cookie }, t);
        foreach (var role in dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole;", transaction: t))
        {
            bool sameAccount = GetCookieAccountId(role.Cookie) == uid;
            bool sameCookie = !string.IsNullOrWhiteSpace(oldCookie) && string.Equals(role.Cookie, oldCookie, StringComparison.Ordinal);
            if (sameAccount || sameCookie)
            {
                dapper.Execute("UPDATE GameRecordRole SET Cookie = @cookie WHERE GameBiz = @GameBiz AND Uid = @Uid;", new { cookie, role.GameBiz, role.Uid }, t);
            }
        }
        t.Commit();
    }


    /// <summary>从工具箱 Cookie 里读出米游社账号 ID（account_id 或 ltuid）。读不到时返回 0。</summary>
    public static long GetCookieAccountId(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return 0;
        }
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }
            if (part.AsSpan(0, index).Trim() is "account_id" or "ltuid")
            {
                if (long.TryParse(part.AsSpan(index + 1).Trim(), out long value))
                {
                    return value;
                }
            }
        }
        return 0;
    }


    /// <summary>
    /// 用一份工具箱 Cookie 拉取国服所有游戏的角色并写库，供通行证账号池同步使用。
    /// <para>
    /// 上游 0.19 的 <see cref="UpdateGameRolesByPassportAsync"/> 会再用 SToken 换一次 Web Cookie，
    /// 而账号池在登录 / 刷新时已经有了现成的 Cookie，这里直接用，避免多余的换票请求。
    /// </para>
    /// </summary>
    /// <param name="cookie">工具箱 Cookie。</param>
    /// <param name="aid">该 Cookie 所属的米游社账号 ID，写入角色行的 <c>Aid</c> 列。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次拉取到的角色。</returns>
    public async Task<List<GameRecordRole>> UpdateGameRolesByCookieAsync(string cookie, long aid, CancellationToken cancellationToken = default)
    {
        var list = new List<GameRecordRole>();
        if (string.IsNullOrWhiteSpace(cookie) || aid <= 0)
        {
            return list;
        }
        foreach (GameBiz gameBiz in ChinaGameBizs)
        {
            try
            {
                var roles = await GetGameRolesAsync(gameBiz, cookie, cancellationToken).ConfigureAwait(false);
                foreach (var role in roles)
                {
                    if (string.IsNullOrWhiteSpace(role.Cookie))
                    {
                        role.Cookie = cookie;
                    }
                    role.Aid = aid;
                }
                list.AddRange(roles);
            }
            catch (miHoYoApiException ex)
            {
                // 这个游戏没有角色时接口会报错，属于正常情况，继续拉下一个游戏。
                _ = ex;
            }
        }
        AddGameRoles(list);
        return list;
    }

    #endregion




    /// <summary>
    /// 删除游戏角色
    /// </summary>
    /// <param name="role"></param>
    public static void DeleteGameRole(GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("DELETE FROM GameRecordRole WHERE GameBiz = @GameBiz AND Uid = @Uid;", role);
    }


}
