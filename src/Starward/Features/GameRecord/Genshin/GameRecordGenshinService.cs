using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.Genshin;
using Starward.Core.GameRecord.Genshin.DailyNote;
using Starward.Core.GameRecord.Genshin.ImaginariumTheater;
using Starward.Core.GameRecord.Genshin.SpiralAbyss;
using Starward.Core.GameRecord.Genshin.StygianOnslaught;
using Starward.Core.GameRecord.Genshin.TravelersDiary;
using Starward.Features.Database;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord.Genshin;

internal class GameRecordGenshinService : GameRecordServiceBase
{


    private readonly ILogger<GameRecordGenshinService> _logger;

    private readonly HyperionGenshinClient _hyperionGenshinClient;

    private readonly HoyolabGenshinClient _hoyolabGenshinClient;



    public GameRecordGenshinService(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<GameRecordGenshinService>>();
        _hyperionGenshinClient = serviceProvider.GetRequiredService<HyperionGenshinClient>();
        _hoyolabGenshinClient = serviceProvider.GetRequiredService<HoyolabGenshinClient>();
    }



    private async Task<IGameRecordGenshinClient> GetClientAsync(GameBiz gameBiz, CancellationToken cancellation = default)
    {
        ServerRegion region = GameBizToServerRegion(gameBiz);
        IGameRecordGenshinClient client = region switch
        {
            ServerRegion.miHoYo => _hyperionGenshinClient,
            ServerRegion.HoYoverse => _hoyolabGenshinClient,
            _ => throw new NotSupportedException($"ServerRegion '{region}' is not supported.")
        };
        client.DeviceInfo = await GetDeviceInfoAsync(region, cancellation);
        client.Language = CultureInfo.CurrentUICulture.Name;
        return client;
    }



    /// <summary>
    /// 获取游戏账号信息
    /// </summary>
    /// <param name="gameBiz"></param>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<GameRecordRole>> GetGameRolesAsync(GameBiz gameBiz, string cookie, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(gameBiz, cancellationToken);
        return await client.GetGameRolesAsync(gameBiz, cookie, cancellationToken);
    }


    /// <summary>
    /// 获取游戏账号头像
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<string> GetGameRoleHeadIconAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        return await client.GetGameRoleHeadIconAsync(role, cancellationToken);
    }



    #region 抽卡 authkey


    /// <summary>
    /// 用米游社 stoken 换取抽卡 authkey（Auth Key B），仅国服支持。
    /// <para>
    /// 用于「从米游社同步」：拿到 authkey 后由
    /// <see cref="BuildGachaLogUrlFromAuthKey"/> 拼出 <c>public-operation</c> 抽卡 URL，
    /// 再交给抽卡记录客户端走既有分页逻辑，不必打开 WebView 抓 URL。
    /// </para>
    /// </summary>
    /// <param name="role">国服角色，提供 game_biz / region / uid。</param>
    /// <param name="stoken">米游社 SToken（V2）；取法见 <see cref="Starward.Core.Passport.PassportCookieParser.Parse"/> 与账号池。</param>
    /// <param name="mid">与 SToken V2 搭配的 mid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>authkey 结果。</returns>
    /// <exception cref="NotSupportedException">国际服角色时抛出。</exception>
    public async Task<GameAuthKey> GenAuthKeyAsync(GameRecordRole role, string stoken, string mid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.GameBiz?.EndsWith("_global", StringComparison.OrdinalIgnoreCase) ?? false)
        {
            throw new NotSupportedException("Generating gacha authkey from HoYoLAB SToken is not supported.");
        }
        // 该接口只有国服有，与工具箱资料一样固定走国服 Client（顺带按需申请设备指纹）。
        _hyperionGenshinClient.DeviceInfo = await GetDeviceInfoAsync(ServerRegion.miHoYo, cancellationToken).ConfigureAwait(false);
        return await _hyperionGenshinClient.GenAuthKeyAsync(role, stoken, mid, cancellationToken).ConfigureAwait(false);
    }



    /// <summary>
    /// 把 genAuthKey 的结果拼成抽卡记录客户端可直接使用的 <c>public-operation</c> URL。
    /// </summary>
    /// <param name="launcherGameBiz">启动器当前选择的区服。</param>
    /// <param name="role">绑定角色，提供 game_biz 与 region。</param>
    /// <param name="authKey">genAuthKey 结果。</param>
    /// <param name="lang">抽卡记录语言；留空时用 zh-cn。</param>
    /// <returns>可直接交给 GetGachaLogAsync 的 URL。</returns>
    /// <exception cref="NotSupportedException">非原神国服（含 B 服）时抛出。</exception>
    public static string BuildGachaLogUrlFromAuthKey(GameBiz launcherGameBiz, GameRecordRole role, GameAuthKey authKey, string? lang = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(authKey);
        if (string.IsNullOrWhiteSpace(authKey.Authkey))
        {
            throw new ArgumentException("Authkey is empty.", nameof(authKey));
        }
        if (launcherGameBiz.Game != GameBiz.hk4e || !(launcherGameBiz.IsChinaServer() || launcherGameBiz.IsBilibili()))
        {
            throw new NotSupportedException($"Building a gacha url from an authkey is only supported for Genshin Impact CN servers (current: {launcherGameBiz}).");
        }
        const string apiPrefix = "https://public-operation-hk4e.mihoyo.com/gacha_info/api/getGachaLog";
        // 查询参数里的 game_biz 用绑定角色的 biz（hk4e_cn），而不是 B 服启动器的 biz。
        string roleGameBiz = string.IsNullOrWhiteSpace(role.GameBiz) ? GameBiz.hk4e_cn : role.GameBiz;
        string language = string.IsNullOrWhiteSpace(lang) ? "zh-cn" : LanguageUtil.FilterLanguage(lang);
        int authkeyVer = authKey.AuthkeyVer > 0 ? authKey.AuthkeyVer : 1;
        int signType = authKey.SignType > 0 ? authKey.SignType : 2;
        var sb = new StringBuilder(apiPrefix);
        sb.Append("?auth_appid=webview_gacha");
        sb.Append("&authkey=").Append(Uri.EscapeDataString(authKey.Authkey));
        sb.Append("&authkey_ver=").Append(authkeyVer);
        sb.Append("&sign_type=").Append(signType);
        sb.Append("&lang=").Append(Uri.EscapeDataString(language));
        sb.Append("&game_biz=").Append(Uri.EscapeDataString(roleGameBiz));
        if (!string.IsNullOrWhiteSpace(role.Region))
        {
            sb.Append("&region=").Append(Uri.EscapeDataString(role.Region));
        }
        return sb.ToString();
    }


    #endregion


    /// <summary>
    /// 原神实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="forceUpdate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<GenshinDailyNote> GetGenshinDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        string key = $"{nameof(GenshinDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_cache.TryGetValue(key, out GenshinDailyNote? note))
        {
            note = await client.GetGenshinDailyNoteAsync(role, cancellationToken);
            _cache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }



    #region 深境螺旋


    /// <summary>
    /// 深境螺旋
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SpiralAbyssInfo> RefreshSpiralAbyssInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        var info = await client.GetSpiralAbyssInfoAsync(role, schedule, cancellationToken);
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.StartTime,
            info.EndTime,
            info.TotalBattleCount,
            info.TotalWinCount,
            info.MaxFloor,
            info.TotalStar,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GenshinSpiralAbyssInfo (Uid, ScheduleId, StartTime, EndTime, TotalBattleCount, TotalWinCount, MaxFloor, TotalStar, Value)
            VALUES (@Uid, @ScheduleId, @StartTime, @EndTime, @TotalBattleCount, @TotalWinCount, @MaxFloor, @TotalStar, @Value);
            """, obj);
        return info;
    }



    public List<SpiralAbyssInfo> GetSpiralAbyssInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<SpiralAbyssInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<SpiralAbyssInfo>("""
            SELECT Uid, ScheduleId, StartTime, EndTime, TotalBattleCount, TotalWinCount, MaxFloor, TotalStar FROM GenshinSpiralAbyssInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public SpiralAbyssInfo? GetSpiralAbyssInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM GenshinSpiralAbyssInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var info = JsonSerializer.Deserialize<SpiralAbyssInfo>(value);
        if (info != null)
        {
            info.Floors = info.Floors.Where(x => x.Index > 8).OrderByDescending(x => x.Index).ToList();
        }
        return info;
    }


    #endregion




    #region 旅行札记



    public async Task<TravelersDiarySummary> GetTravelersDiarySummaryAsync(GameRecordRole role, int month = 0, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        var summary = await client.GetTravelsDiarySummaryAsync(role, month, cancellationToken);
        if (summary.MonthData is null)
        {
            return summary;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GenshinTravelersDiaryMonthData
            (Uid, Year, Month, CurrentPrimogems, CurrentMora, LastPrimogems, LastMora, CurrentPrimogemsLevel, PrimogemsChangeRate, MoraChangeRate, PrimogemsGroupBy)
            VALUES (@Uid, @Year, @Month, @CurrentPrimogems, @CurrentMora, @LastPrimogems, @LastMora, @CurrentPrimogemsLevel, @PrimogemsChangeRate, @MoraChangeRate, @PrimogemsGroupBy);
            """, summary.MonthData);
        return summary;
    }


    public List<TravelersDiaryMonthData> GetTravelersDiaryMonthDataList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<TravelersDiaryMonthData>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<TravelersDiaryMonthData>("SELECT * FROM GenshinTravelersDiaryMonthData WHERE Uid = @Uid ORDER BY Year DESC, Month DESC;", new { role.Uid });
        return list.ToList();
    }



    public async Task<int> GetTravelersDiaryDetailAsync(GameRecordRole role, int month, int type, int limit = 100, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        var detail = await client.GetTravelsDiaryDetailAsync(role, month, type, limit, cancellationToken);
        if (detail.List is null || detail.List.Count == 0)
        {
            return 0;
        }
        var list = detail.List;
        using var dapper = DatabaseService.CreateConnection();
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM GenshinTravelersDiaryAwardItem WHERE Uid=@Uid AND Year=@Year AND Month=@Month AND Type=@Type;", list.FirstOrDefault());
        if (existCount == list.Count)
        {
            return 0;
        }
        using var t = dapper.BeginTransaction();
        dapper.Execute($"DELETE FROM GenshinTravelersDiaryAwardItem WHERE Uid=@Uid AND Year=@Year AND Month=@Month AND Type=@Type;", list.FirstOrDefault(), t);
        dapper.Execute("""
                INSERT INTO GenshinTravelersDiaryAwardItem (Uid, Year, Month, Type, ActionId, ActionName, Time, Number)
                VALUES (@Uid, @Year, @Month, @Type, @ActionId, @ActionName, @Time, @Number);
                """, list, t);
        t.Commit();
        return list.Count - existCount;
    }



    public List<TravelersDiaryAwardItem> GetTravelersDiaryDetailItems(long uid, int year, int month, int type)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<TravelersDiaryAwardItem>("SELECT * FROM GenshinTravelersDiaryAwardItem WHERE Uid=@uid AND Year=@year AND Month=@month AND Type=@type ORDER BY Time;", new { uid, year, month, type }).ToList();
    }




    #endregion




    #region 幻想真境剧诗



    /// <summary>
    /// 幻想真境剧诗
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task RefreshImaginariumTheaterInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        var infos = await client.GetImaginariumTheaterInfosAsync(role, cancellationToken);
        if (infos.Count == 0)
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        foreach (var info in infos)
        {
            var obj = new
            {
                info.Uid,
                info.ScheduleId,
                info.StartTime,
                info.EndTime,
                info.DifficultyId,
                info.MaxRoundId,
                info.Heraldry,
                info.MedalNum,
                Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
            INSERT OR REPLACE INTO GenshinImaginariumTheaterInfo (Uid, ScheduleId, StartTime, EndTime, DifficultyId, MaxRoundId, Heraldry, MedalNum, Value)
            VALUES (@Uid, @ScheduleId, @StartTime, @EndTime, @DifficultyId, @MaxRoundId, @Heraldry, @MedalNum, @Value);
            """, obj, t);
        }
        t.Commit();
    }




    public List<ImaginariumTheaterInfo> GetImaginariumTheaterInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ImaginariumTheaterInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ImaginariumTheaterInfo>("""
            SELECT Uid, ScheduleId, StartTime, EndTime, DifficultyId, MaxRoundId, Heraldry, MedalNum FROM GenshinImaginariumTheaterInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ImaginariumTheaterInfo? GetImaginariumTheaterInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM GenshinImaginariumTheaterInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ImaginariumTheaterInfo>(value);
    }




    #endregion




    #region 幽境危战



    public async Task<List<StygianOnslaughtInfo>> RefreshStygianOnslaughtInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(role.GameBiz, cancellationToken);
        var infos = await client.GetStygianOnslaughtInfosAsync(role, cancellationToken);
        if (infos.Count == 0)
        {
            return infos;
        }
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        foreach (var info in infos)
        {
            var obj = new
            {
                info.Uid,
                info.ScheduleId,
                info.StartDateTime,
                info.EndDateTime,
                info.Difficulty,
                info.Second,
                Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
            INSERT OR REPLACE INTO GenshinStygianOnslaughtInfo (Uid, ScheduleId, StartDateTime, EndDateTime, Difficulty, Second, Value)
            VALUES (@Uid, @ScheduleId, @StartDateTime, @EndDateTime, @Difficulty, @Second, @Value);
            """, obj, t);
        }
        t.Commit();
        return infos;
    }



    public List<StygianOnslaughtInfo> GetStygianOnslaughtInfoList(GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<StygianOnslaughtInfo>("""
            SELECT Uid, ScheduleId, StartDateTime, EndDateTime, Difficulty, Second FROM GenshinStygianOnslaughtInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public StygianOnslaughtInfo? GetStygianOnslaughtInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM GenshinStygianOnslaughtInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<StygianOnslaughtInfo>(value);
    }



    #endregion


}
