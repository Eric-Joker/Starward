using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.GameRecord.ZZZ.DeadlyAssault;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.ZZZ.InterKnotReport;
using Starward.Core.GameRecord.ZZZ.ShiyuDefense;
using Starward.Core.GameRecord.ZZZ.ThresholdSimulation;
using Starward.Core.GameRecord.ZZZ.UpgradeGuide;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starward.Core.GameRecord.ZZZ;

public class HoyolabZZZClient : GameRecordClientBase, IGameRecordClient, IGameRecordZZZClient
{

    public override string AppVersion => HoyolabAppVersion;

    public override string DefaultUAContent => HoyolabUAContent;

    public override ServerRegion ServerRegion => ServerRegion.HoYoverse;


    public HoyolabZZZClient(HttpClient? httpClient = null) : base(httpClient)
    {

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
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie));
        }
        string url = $"https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?game_biz={gameBiz}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, cookie);
        request.Headers.Add(X_Request_With, com_mihoyo_hoyolab);
        request.Headers.Add(Referer, "https://act.hoyolab.com");
        var data = await CommonSendAsync<GameRecordRoleWrapper>(request, cancellationToken).ConfigureAwait(false);
        if (data.List is not null)
        {
            foreach (var item in data.List)
            {
                item.Cookie = cookie;
                try
                {
                    item.HeadIcon = await GetGameRoleHeadIconAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (miHoYoApiException) { }
            }
        }
        return data.List ?? [];
    }


    /// <summary>
    /// 获取游戏账号头像
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<string> GetGameRoleHeadIconAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/index?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        var data = await CommonSendAsync<GameRecordIndex>(request, cancellationToken).ConfigureAwait(false);
        return data.HeadIcon;
    }


    /// <summary>
    /// 绝区零抽卡记录
    /// </summary>
    /// <param name="role"></param>
    /// <param name="gachaType">ZZZGachaType</param>
    /// <param name="endId">首次请求不传</param>
    /// <param name="language"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ZZZGachaRecordData> GetZZZGachaRecordAsync(GameRecordRole role, int gachaType, long? endId = null, string? language = null, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/gacha_record?uid={role.Uid}&region={role.Region}&gacha_type={gachaType}";
        long validEndId = endId.GetValueOrDefault();
        if (validEndId > 0)
        {
            url += $"&end_id={validEndId}";
        }
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        return await CommonSendAsync<ZZZGachaRecordData>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 式舆防卫战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ShiyuDefenseWrapper> GetShiyuDefenseInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/hadal_info_v2?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}&need_all=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(X_Request_With, com_mihoyo_hoyolab);
        return await CommonSendAsync<ShiyuDefenseWrapper>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 危局强袭战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<DeadlyAssaultInfo> GetDeadlyAssaultInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/hadal_mem_detail_v2?schedule_type={schedule}&region={role.Region}&uid={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(X_Request_With, com_mihoyo_hoyolab);
        return await CommonSendAsync<DeadlyAssaultInfo>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 绳网月报总结
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<InterKnotReportSummary> GetInterKnotReportSummaryAsync(GameRecordRole role, string month = "", CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/nap_ledger/month_info?uid={role.Uid}&region={role.Region}&month={month}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(X_Request_With, com_mihoyo_hoyolab);
        return await CommonSendAsync<InterKnotReportSummary>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 绳网月报收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="type"></param>
    /// <param name="page">从1开始</param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回一页收入记录</returns>
    public async Task<InterKnotReportDetail> GetInterKnotReportDetailByPageAsync(GameRecordRole role, string month, string type, int page, int page_size = 100, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/nap_ledger/month_detail?uid={role.Uid}&region={role.Region}&month={month}&type={type}&current_page={page}&page_size={page_size}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(X_Request_With, com_mihoyo_hoyolab);
        return await CommonSendAsync<InterKnotReportDetail>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 绳网月报收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="type"></param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回该月所有收入记录</returns>
    public async Task<InterKnotReportDetail> GetInterKnotReportDetailAsync(GameRecordRole role, string month, string type, int page_size = 100, CancellationToken cancellationToken = default)
    {
        page_size = Math.Clamp(page_size, 20, 100);
        var data = await GetInterKnotReportDetailByPageAsync(role, month, type, 1, page_size, cancellationToken).ConfigureAwait(false);
        if (data.List.Count < page_size)
        {
            return data;
        }
        for (int i = 2; ; i++)
        {
            var addData = await GetInterKnotReportDetailByPageAsync(role, month, type, i, page_size, cancellationToken).ConfigureAwait(false);
            data.List.AddRange(addData.List);
            if (addData.List.Count < page_size)
            {
                break;
            }
        }
        return data;
    }


    /// <summary>
    /// 绝区零实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ZZZDailyNote> GetZZZDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        return await CommonSendAsync<ZZZDailyNote>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 绝区零临界推演
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ThresholdSimulationAbstractInfo> GetZZZThresholdSimulationAbstractInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/void_front_battle_period_abstract_info?region={role.Region}&uid={role.Uid}&schedule_type=1";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        return await CommonSendAsync<ThresholdSimulationAbstractInfo>(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 绝区零临界推演
    /// </summary>
    /// <param name="role"></param>
    /// <param name="void_front_id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ThresholdSimulationDetailInfo> GetZZZThresholdSimulationDetailInfoAsync(GameRecordRole role, int void_front_id, CancellationToken cancellationToken = default)
    {
        string url = $"https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/void_front_battle_period_detail?region={role.Region}&uid={role.Uid}&void_front_id={void_front_id}&schedule_type=1";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        var data = await CommonSendAsync<ThresholdSimulationPeriodDetail>(request, cancellationToken).ConfigureAwait(false);
        return data.Detail;
    }


    /// <summary>
    /// 国际服养成指南 badge 登录，换取 <c>e_nap_token</c>。
    /// 主机 <c>sg-act-public-api</c>；浏览器 UA + act 专用 Gen1 DS 盐；禁止 <c>x-rpc-device_id</c>。
    /// </summary>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">语言代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并 e_nap_token 后的 Cookie 串。</returns>
    public async Task<string> LoginZZZCultivateBadgeAsync(GameRecordRole role, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        string lang = LanguageUtil.FilterLanguage(language);
        Language = lang;
        var body = new ZZZCultivateBadgeLoginBody
        {
            GameBiz = "nap_global",
            Lang = lang,
            Region = role.Region,
            Uid = role.Uid.ToString(),
        };
        const string url = "https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account";
        string json = JsonSerializer.Serialize(body, typeof(ZZZCultivateBadgeLoginBody), GameRecordJsonContext.Default);
        string cookie = ApplyMi18nLangToCookie(role.Cookie, lang);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, application_json),
        };
        AddHeader(request, Cookie, cookie);
        AddHeader(request, Referer, "https://act.hoyolab.com/zzz/gt/character-builder-h/index.html");
        AddHeader(request, "Origin", "https://act.hoyolab.com");
        AddHeader(request, x_rpc_app_version, "1.5.0");
        AddHeader(request, x_rpc_client_type, "5");
        AddHeader(request, x_rpc_language, lang);
        AddHeader(request, x_rpc_lang, lang);
        if (!string.IsNullOrWhiteSpace(DeviceInfo?.DeviceFp))
        {
            AddHeader(request, x_rpc_device_fp, DeviceInfo.DeviceFp);
        }
        // 国际服 act 用 genshin.py OVERSEAS Gen1 salt，不是 BBS 接口的盐
        AddHeader(request, DS, CreateCultivateSecret1(CultivateToolDsSaltOverseas));
        var (wrapper, response) = await SendCultivateBadgeLoginAsync(request, cancellationToken).ConfigureAwait(false);
        if (wrapper is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.");
        }
        if (wrapper.Retcode != 0)
        {
            throw new miHoYoApiException(wrapper.Retcode, wrapper.Message);
        }
        return MergeCookieFromSetCookie(cookie, response);
    }


    /// <summary>
    /// 将 Cookie 中的 <c>mi18nLang</c> 设为指定语言；不存在则追加，便于多语言循环请求。
    /// </summary>
    /// <param name="cookie">原始 Cookie；可为 null。</param>
    /// <param name="lang">语言代码。</param>
    /// <returns>带 <c>mi18nLang</c> 的 Cookie。</returns>
    private static string ApplyMi18nLangToCookie(string? cookie, string lang)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return $"mi18nLang={lang}";
        }
        if (cookie.Contains("mi18nLang=", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(cookie, @"mi18nLang=[^;]*", $"mi18nLang={lang}", RegexOptions.IgnoreCase);
        }
        return cookie.TrimEnd(' ', ';') + $"; mi18nLang={lang}";
    }

}
