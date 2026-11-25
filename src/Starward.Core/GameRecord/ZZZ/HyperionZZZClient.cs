using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.GameRecord.ZZZ.DeadlyAssault;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.ZZZ.GachaStat;
using Starward.Core.GameRecord.ZZZ.InterKnotReport;
using Starward.Core.GameRecord.ZZZ.ShiyuDefense;
using Starward.Core.GameRecord.ZZZ.ThresholdSimulation;
using Starward.Core.GameRecord.ZZZ.UpgradeGuide;
using System.Text;
using System.Text.Json;

namespace Starward.Core.GameRecord.ZZZ;

public class HyperionZZZClient : GameRecordClientBase, IGameRecordClient, IGameRecordZZZClient
{

    public override string AppVersion => HyperionAppVersion;

    public override string DefaultUAContent => HyperionUAContent;

    public override ServerRegion ServerRegion => ServerRegion.miHoYo;


    public HyperionZZZClient(HttpClient? httpClient = null) : base(httpClient)
    {

    }



    protected override void AddDeviceHeaders(HttpRequestMessage request)
    {
        base.AddDeviceHeaders(request);
        AddHeader(request, x_rpc_lang, Language);
        AddHeader(request, x_rpc_language, Language);
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
        string url = $"https://passport-api.mihoyo.com/binding/api/getUserGameRolesByCookieToken?game_biz={gameBiz}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/index?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/gacha_record?uid={role.Uid}&region={role.Region}&gacha_type={gachaType}";
        long validEndId = endId.GetValueOrDefault();
        if (validEndId > 0)
        {
            url += $"&end_id={validEndId}";
        }
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/hadal_info_v2?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}&need_all=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/hadal_mem_detail_v2?schedule_type={schedule}&region={role.Region}&uid={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
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
        string url = $"https://api-takumi.mihoyo.com/event/nap_ledger/month_info?uid={role.Uid}&region={role.Region}&month={month}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
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
        string url = $"https://api-takumi.mihoyo.com/event/nap_ledger/month_detail?uid={role.Uid}&region={role.Region}&month={month}&type={type}&current_page={page}&page_size={page_size}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/note?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/void_front_battle_period_abstract_info?region={role.Region}&uid={role.Uid}&schedule_type=1";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
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
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/void_front_battle_period_detail?region={role.Region}&uid={role.Uid}&void_front_id={void_front_id}&schedule_type=1";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        var data = await CommonSendAsync<ThresholdSimulationPeriodDetail>(request, cancellationToken).ConfigureAwait(false);
        return data.Detail;
    }


    /// <summary>
    /// 国服养成指南 badge 登录，换取 <c>e_nap_token</c>。
    /// 对齐 genshin.py：浏览器 UA、Gen2 DS、禁止 <c>x-rpc-device_id</c>（否则 -100 / 易触发 10035）。
    /// </summary>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">语言代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并 e_nap_token 后的 Cookie 串。</returns>
    public async Task<string> LoginZZZCultivateBadgeAsync(GameRecordRole role, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        string lang = LanguageUtil.FilterLanguage(language);
        var body = new ZZZCultivateBadgeLoginBody
        {
            GameBiz = "nap_cn",
            Lang = lang,
            Region = role.Region,
            Uid = role.Uid.ToString(),
        };
        const string url = "https://api-takumi.mihoyo.com/common/badge/v1/login/account";
        string json = JsonSerializer.Serialize(body, typeof(ZZZCultivateBadgeLoginBody), GameRecordJsonContext.Default);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, application_json),
        };
        AddHeader(request, Cookie, role.Cookie);
        AddHeader(request, Referer, "https://act.mihoyo.com/zzz/gt/character-builder-h/index.html");
        AddHeader(request, "Origin", "https://act.mihoyo.com");
        // genshin.py 对 CN act DS 头使用较低 app_version；过高 + BBS UA 易 10035
        AddHeader(request, x_rpc_app_version, "2.11.1");
        AddHeader(request, x_rpc_client_type, "5");
        if (!string.IsNullOrWhiteSpace(DeviceInfo?.DeviceFp))
        {
            AddHeader(request, x_rpc_device_fp, DeviceInfo.DeviceFp);
        }
        AddHeader(request, DS, CreateCultivateSecret2(url, body));
        var (wrapper, response) = await SendCultivateBadgeLoginAsync(request, cancellationToken).ConfigureAwait(false);
        if (wrapper is null)
        {
            throw new miHoYoApiException(-1, "Can not parse the response body.");
        }
        if (wrapper.Retcode != 0)
        {
            throw new miHoYoApiException(wrapper.Retcode, wrapper.Message);
        }
        return MergeCookieFromSetCookie(role.Cookie, response);
    }


    /// <summary>
    /// 拉取官方抽卡统计（米游社「抽卡记录」活动页）：总览 + 各频段全部历史 S 级记录。
    /// </summary>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">期望语言；国服记录名称固定中文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>官方统计。</returns>
    public Task<ZZZOfficialGachaStat> GetZZZOfficialGachaStatAsync(GameRecordRole role, string? language = null, CancellationToken cancellationToken = default)
    {
        return ZZZOfficialGachaStatFetcher.FetchAsync(this, role, language, cancellationToken);
    }


    /// <summary>
    /// 官方抽卡统计总览（国服 act-api-takumi）。
    /// </summary>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">含 <c>e_nap_token</c> 的 Cookie。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>总览数据。</returns>
    public Task<ZZZGachaStatIndex> GetZZZGachaStatIndexAsync(GameRecordRole role, string cookie, CancellationToken cancellationToken = default)
    {
        var url = $"https://act-api-takumi.mihoyo.com/event/nap_gacha_record/api/zzz/index?uid={role.Uid}&region={role.Region}";
        return SendZZZGachaStatRequestAsync<ZZZGachaStatIndex>(url, cookie, cancellationToken);
    }


    /// <summary>
    /// 官方抽卡统计当前版本卡池（国服 act-api-takumi）。
    /// </summary>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">含 <c>e_nap_token</c> 的 Cookie。</param>
    /// <param name="gachaType">ZZZGachaType 整数值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前版本卡池。</returns>
    public Task<ZZZGachaPoolStatData> GetZZZGachaCurrentPoolStatAsync(GameRecordRole role, string cookie, int gachaType, CancellationToken cancellationToken = default)
    {
        var url = $"https://act-api-takumi.mihoyo.com/event/nap_gacha_record/api/zzz/cur_pool_stat?uid={role.Uid}&region={role.Region}&gacha_type={gachaType}";
        return SendZZZGachaStatRequestAsync<ZZZGachaPoolStatData>(url, cookie, cancellationToken);
    }


    /// <summary>
    /// 官方抽卡统计历史版本（国服 act-api-takumi）。
    /// </summary>
    /// <param name="role">绝区零角色。</param>
    /// <param name="cookie">含 <c>e_nap_token</c> 的 Cookie。</param>
    /// <param name="gachaType">ZZZGachaType 整数值。</param>
    /// <param name="versions">要查询的版本号，调用方保证每次不超过 8 个。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>历史版本的 S 级记录。</returns>
    public Task<ZZZGachaPoolStatData> GetZZZGachaHistoryPoolStatAsync(GameRecordRole role, string cookie, int gachaType, IReadOnlyList<string> versions, CancellationToken cancellationToken = default)
    {
        var url = $"https://act-api-takumi.mihoyo.com/event/nap_gacha_record/api/zzz/hist_pool_stat?uid={role.Uid}&region={role.Region}&gacha_type={gachaType}&vers={string.Join(',', versions)}";
        return SendZZZGachaStatRequestAsync<ZZZGachaPoolStatData>(url, cookie, cancellationToken);
    }


    /// <summary>
    /// 发送官方抽卡统计请求。与米游社 App 内活动页一致只靠 Cookie（e_nap_token + 设备指纹），
    /// 不带 DS 与 x-rpc 头；桌面浏览器 UA 实测可用。
    /// </summary>
    /// <typeparam name="T">响应数据类型。</typeparam>
    /// <param name="url">完整请求地址。</param>
    /// <param name="cookie">badge 登录后合并的 Cookie。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应 data。</returns>
    private Task<T> SendZZZGachaStatRequestAsync<T>(string url, string cookie, CancellationToken cancellationToken) where T : class
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeader(request, Cookie, MergeDeviceFpCookies(cookie));
        AddHeader(request, Referer, "https://act.mihoyo.com/");
        return CommonSendCultivateAsync<T>(request, cancellationToken);
    }


    /// <summary>
    /// 把设备指纹写入 Cookie，对齐官方活动页携带的 <c>DEVICEFP</c> / <c>DEVICEFP_SEED_ID</c> / <c>DEVICEFP_SEED_TIME</c> / <c>_MHYUUID</c>。
    /// </summary>
    /// <param name="cookie">原始 Cookie；可为 null。</param>
    /// <returns>合并设备指纹后的 Cookie。</returns>
    private string MergeDeviceFpCookies(string? cookie)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
                }
            }
        }
        if (DeviceInfo is { } info)
        {
            // 与 badge 登录的 x-rpc-device_fp 用同一枚指纹；getFp 失败时两者都已退化为本地随机值
            string? deviceFp = !string.IsNullOrWhiteSpace(info.DeviceFp) ? info.DeviceFp : info.BbsDeviceFp;
            if (!string.IsNullOrWhiteSpace(deviceFp))
            {
                map["DEVICEFP"] = deviceFp;
            }
            if (!string.IsNullOrWhiteSpace(info.SeedId))
            {
                map["DEVICEFP_SEED_ID"] = info.SeedId;
            }
            if (!string.IsNullOrWhiteSpace(info.SeedTime))
            {
                map["DEVICEFP_SEED_TIME"] = info.SeedTime;
            }
            if (!string.IsNullOrWhiteSpace(info.BbsDeviceId))
            {
                map["_MHYUUID"] = info.BbsDeviceId;
            }
        }
        return string.Join("; ", map.Select(static kv => $"{kv.Key}={kv.Value}"));
    }



    #region stoken 换抽卡 authkey


    /// <summary>
    /// 用米游社 stoken 换取抽卡 authkey（Auth Key B），仅国服提供该接口。
    /// </summary>
    /// <param name="role">游戏角色，提供 game_biz / region / uid。</param>
    /// <param name="stoken">米游社 SToken（V2）。</param>
    /// <param name="mid">与 SToken V2 搭配的 mid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>含 authkey / authkey_ver / sign_type 的结果。</returns>
    public override Task<GameAuthKey> GenAuthKeyAsync(GameRecordRole role, string stoken, string mid, CancellationToken cancellationToken = default)
    {
        return GenAuthKeyCoreAsync(role, stoken, mid, cancellationToken);
    }


    #endregion


}
