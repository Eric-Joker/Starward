using System.Text.Json;
using Starward.Core.GameRecord.BH3.DailyNote;
using Starward.Core.GameRecord.Genshin.DailyNote;
using Starward.Core.GameRecord.Genshin.ImaginariumTheater;
using Starward.Core.GameRecord.Genshin.SpiralAbyss;
using Starward.Core.GameRecord.Genshin.StygianOnslaught;
using Starward.Core.GameRecord.Genshin.TravelersDiary;
using Starward.Core.GameRecord.StarRail.ApocalypticShadow;
using Starward.Core.GameRecord.StarRail.ChallengePeak;
using Starward.Core.GameRecord.StarRail.DailyNote;
using Starward.Core.GameRecord.StarRail.ForgottenHall;
using Starward.Core.GameRecord.StarRail.PureFiction;
using Starward.Core.GameRecord.StarRail.SimulatedUniverse;
using Starward.Core.GameRecord.StarRail.TrailblazeCalendar;
using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.GameRecord.ZZZ.DeadlyAssault;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.ZZZ.InterKnotReport;
using Starward.Core.GameRecord.ZZZ.ShiyuDefense;
using Starward.Core.GameRecord.ZZZ.ThresholdSimulation;
using Starward.Core.GameRecord.ZZZ.UpgradeGuide;
using Starward.Core.GameRecord.SignIn;

namespace Starward.Core.GameRecord;


public class HyperionClient : GameRecordClient
{


    /// <summary>
    /// 默认设备名（UA、<c>x-rpc-device_name</c> 与 getFp 的 body 共用）。UA 里长这样：<c>Android 13; Pixel 5 Build/TQ3A.230901.001</c>。
    /// <para>
    /// 默认值是一台仓库自带的公开机型（Pixel 5），不是任何人的实机，免得 UA 自称一台机器、
    /// body 里的 ext_fields 又说另一台。需要真实机型时由用户自己填，见 <see cref="DeviceName"/>。
    /// </para>
    /// </summary>
    public const string DefaultDeviceName = "Pixel 5";

    /// <summary>默认 Android 版本，与 <see cref="HyperionDeviceProfile.Default"/> 的 build 串、SDK 版本对应。</summary>
    public const string DefaultSystemVersion = "13";

    /// <summary>默认米游社 App 版本，取抓包里的值。</summary>
    public const string DefaultAppVersion = "2.114.0";

    /// <summary>
    /// 默认 Android WebView / Chromium 版本，取仓库自带的 Pixel 5 机型标识对应的那一版。
    /// 这个版本由系统 WebView 决定，与 <see cref="HyperionDeviceProfile"/> 无关，所以单独放在这里；
    /// 与 <see cref="UAContent"/> 里的 Android 13、<see cref="DefaultDeviceName"/> 同属一套公开机型标识。
    /// </summary>
    public const string DefaultWebViewVersion = "118.0.0.0";



    /// <summary>
    /// 设备名，UA、<c>x-rpc-device_name</c> 与 getFp 的 body 三处共用同一个值。
    /// <para>
    /// 写入前先用 <see cref="NormalizeDeviceName(string?)"/> 归一：用户从抓包里整段复制进来的
    /// <c>x-rpc-device_name</c> 是已经百分号转义过的，直接当机型用会被再转义一次。
    /// </para>
    /// </summary>
    public string DeviceName { get; set; } = DefaultDeviceName;

    /// <summary>Android 版本，UA、<c>x-rpc-sys_version</c> 与 getFp 的 body 三处共用同一个值。</summary>
    public string SystemVersion { get; set; } = DefaultSystemVersion;


    /// <summary>
    /// 把设备名归一成人类可读的形式：<c>Pixel%205</c> 与 <c>Pixel 5</c> 都归一成后者。
    /// <para>
    /// 抓包里的 <c>x-rpc-device_name</c> 本身就是转义过的（例如 <c>Pixel%205</c>），
    /// 整段粘进工具箱就会被当成机型存下来；UA 与请求头再各自转义一次，
    /// 发出去就变成 <c>Pixel%25205</c>，服务端看到的是双重转义的机型。
    /// 因此使用前统一解码一次：UA 用 <c>Pixel 5</c>，
    /// <c>x-rpc-device_name</c> 用一次 <see cref="Uri.EscapeDataString"/> 得到的 <c>Pixel%205</c>，两者与抓包一致。
    /// </para>
    /// <para>
    /// 不含 <c>%XX</c> 转义时原样返回，普通的 <c>%</c>（例如 <c>100%</c>）不受影响；
    /// 转义序列不合法时也原样返回，不会抛异常。
    /// </para>
    /// </summary>
    /// <param name="deviceName">配置里存着的设备名；可为空。</param>
    /// <returns>解码后的设备名；没有可解码的转义序列时是去掉首尾空白的原值。</returns>
    public static string NormalizeDeviceName(string? deviceName)
    {
        string value = deviceName?.Trim() ?? "";
        if (value.Length == 0 || !ContainsPercentEscape(value))
        {
            return value;
        }
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception)
        {
            // 非法转义序列（落单的 % 或 %ZZ）当作普通字符，原样返回。
            return value;
        }
    }


    /// <summary>
    /// 判断字符串里是否含 <c>%XX</c> 形式的百分号转义（XX 为十六进制）。
    /// 只有这种序列才需要解码，其余的 <c>%</c> 是用户输入的一部分。
    /// </summary>
    /// <param name="value">待检查的字符串。</param>
    /// <returns>存在转义序列时返回 true。</returns>
    private static bool ContainsPercentEscape(string value)
    {
        for (int i = 0; i + 2 < value.Length; i++)
        {
            if (value[i] is '%' && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                return true;
            }
        }
        return false;
    }


    /// <summary>首次运行时给设备 ID 一个初始值，之后一直沿用。</summary>
    public static string GenerateDeviceId() => Guid.NewGuid().ToString("D");


    /// <summary>
    /// 当前 getFp 的 <c>ext_fields</c> 载荷版本。
    /// 旧版本的载荷里写死了 Windows 的硬件字段，服务端会把设备判成模拟器，
    /// 所以升级后必须强制重刷一次指纹，不能继续用旧载荷申请到的指纹。
    /// </summary>
    public const int DeviceFpPayloadVersion = 2;


    /// <summary>
    /// getFp 请求体里的 <c>device_id</c>。真机上是 16 位十六进制的 ANDROID_ID，
    /// 与 <see cref="DeviceId"/>（米游社自己的设备号）不是同一个值，但同样要跨刷新稳定复用。
    /// </summary>
    public string AndroidId { get; set; } = "";


    // DeviceFpSeedId / DeviceFpSeedTime 已上移到基类 GameRecordClient：
    // 战绩 Cookie 合并（DEVICEFP_SEED_ID / DEVICEFP_SEED_TIME）与 getFp 需要共用同一组值。
    // AndroidId 仍留在本类，由 getFp 单独使用。


    /// <summary>
    /// getFp 载荷里无法由设备名推导的机型字段。
    /// <para>
    /// 取值必须来自一台公开存在、且与 UA、<c>x-rpc-device_name</c> 自洽的 Android 手机；
    /// 默认取仓库自带的 Pixel 5，不写任何人的实机。**这份档案是固定的**：
    /// 实机验证过"机型身份与账号是否匹配"不影响接口结果（中性 Pixel 5 / Android 13 时便笺照样正常，
    /// 10041 只与"这枚指纹有没有被账号登记"有关），所以不要再把它做成可配置项。
    /// </para>
    /// </summary>
    /// <param name="Brand">Build.BRAND。同时是 <c>deviceInfo</c> 串的第一段。</param>
    /// <param name="Manufacturer">Build.MANUFACTURER。</param>
    /// <param name="ProductName">Build.PRODUCT / Build.DEVICE，通常是机型代号；同时也是 <c>hostname</c>、<c>deviceType</c> 与 <c>board</c>。</param>
    /// <param name="Hardware">Build.HARDWARE，通常是 SoC 代号。</param>
    /// <param name="BuildId">Build.ID / Build.DISPLAY，同时也是 UA 里的 build 串与 <c>display</c>。</param>
    /// <param name="Incremental">Build.VERSION.INCREMENTAL，厂商版本号。</param>
    /// <param name="BuildTime">Build.TIME，毫秒时间戳。</param>
    public sealed record HyperionDeviceProfile(
        string Brand,
        string Manufacturer,
        string ProductName,
        string Hardware,
        string BuildId,
        string Incremental,
        long BuildTime)
    {
        /// <summary>
        /// 默认机型：Pixel 5（<c>redfin</c>）Android 13，与 <see cref="DefaultDeviceName"/>、<see cref="DefaultSystemVersion"/> 对应。
        /// <para>
        /// 各字段互相对应：<c>google/redfin/redfin:13/TQ3A.230901.001/10161073:user/release-keys</c>
        /// 即 <see cref="Brand"/>/<see cref="ProductName"/>/<see cref="ProductName"/>:13/<see cref="BuildId"/>/<see cref="Incremental"/>；
        /// Android 13 在 <see cref="GetApiLevel"/> 里映射到 API level 33，<see cref="BuildTime"/> 是 Android 13 那版 build 的时间戳。
        /// </para>
        /// </summary>
        public static HyperionDeviceProfile Default { get; } = new(
            "google",
            "Google",
            "redfin",
            "redfin",
            "TQ3A.230901.001",
            "10161073",
            1692921600000);
    }


    /// <summary>当前使用的机型字段，默认取 <see cref="HyperionDeviceProfile.Default"/>（仓库自带的中性档案）。</summary>
    public HyperionDeviceProfile DeviceProfile { get; set; } = HyperionDeviceProfile.Default;



    /// <summary>
    /// 战绩与签到接口共用的米游社 App UA，所有游戏走同一个值（见 <see cref="CommonSendAsync"/>）。
    /// <para>
    /// 机型、Android 版本、build 串、App 版本全部从 <see cref="DeviceName"/>、<see cref="SystemVersion"/>、
    /// <see cref="DeviceProfile"/>、<see cref="AppVersion"/> 推导，不要再写死字面量：
    /// UA 与同一请求里 <c>x-rpc-device_name</c> / <c>x-rpc-sys_version</c> / getFp 的
    /// <c>ext_fields.deviceInfo</c> 必须描述同一台机器，否则风控一眼就能看出是拼出来的。
    /// </para>
    /// <para>
    /// 这里用 <c>{DeviceName}</c> 而不是 <c>{Build.MANUFACTURER} {Build.MODEL}</c>：
    /// 同一个值还要进 <c>x-rpc-device_name</c>，官方抓包那里是带厂商前缀的
    /// （例如 <c>Pixel%205</c>），但用户可在设置里手填设备名，
    /// 推导厂商前缀会产生「Google Pixel 5 Pixel 5」这种重复，所以保持与 <c>x-rpc-device_name</c> 完全一致。
    /// </para>
    /// </summary>
    public override string UAContent => $"Mozilla/5.0 (Linux; Android {SystemVersion}; {DeviceName} Build/{DeviceProfile.BuildId}; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/{DefaultWebViewVersion} Mobile Safari/537.36 miHoYoBBS/{AppVersion}";


    public override string AppVersion => AppVersionOverride;


    /// <summary>
    /// 米游社 App 版本。同时用于 UA 与 <c>x-rpc-app_version</c> 请求头，
    /// 由上层从配置写入，默认取真机抓包里的值。
    /// </summary>
    public string AppVersionOverride { get; set; } = DefaultAppVersion;

    protected override string ApiSalt => "t0qEgfub6cvueAPgR5m9aQWWVciEer7v";

    protected override string ApiSalt2 => "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";

    protected override string ActOrigin => "https://act.mihoyo.com";

    protected override string XRequestedWithValue => com_mihoyo_hyperion;

    /// <summary>战绩 H5 的 <c>x-rpc-device_name</c>，与 UA、getFp 的 body 共用同一个设备名。</summary>
    protected override string RpcDeviceName => DeviceName;

    /// <summary>战绩 H5 的 <c>x-rpc-sys_version</c>，与 UA、getFp 的 body 共用同一个系统版本。</summary>
    protected override string RpcSysVersionValue => SystemVersion;



    public HyperionClient(HttpClient? httpClient = null) : base(httpClient)
    {

    }




    /// <summary>
    /// 国服（米游社）签到请求头：Referer 用 webstatic，带 <c>x-rpc-signgame</c> 区分游戏；
    /// 绝区零活动主机需要它自己的 Origin，其余游戏用默认 act.mihoyo.com。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="config">签到活动配置，提供 signgame / origin。</param>
    /// <param name="signData">是否为数据接口（保留参数，当前与 home 共用相同请求头）。</param>
    protected override void AddSignInPlatformHeaders(HttpRequestMessage request, SignInActivityConfig config, bool signData)
    {
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add("x-rpc-signgame", config.SignGame);
        request.Headers.TryAddWithoutValidation(Origin, string.IsNullOrEmpty(config.Origin) ? ActOrigin : config.Origin);
        request.Headers.TryAddWithoutValidation(X_Request_With, XRequestedWithValue);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(x_rpc_device_id, DeviceId);
        request.Headers.Add(x_rpc_device_fp, DeviceFp);
    }



    // https://webstatic.mihoyo.com/bbs/event/signin-ys/index.html?bbs_auth_required=true&act_id=e202009291139501&utm_source=bbs&utm_medium=mys&utm_campaign=icon
    // https://webstatic.mihoyo.com/ys/event/e20200709ysjournal/index.html?bbs_presentation_style=fullscreen&bbs_auth_required=true&utm_source=bbs&utm_medium=mys&utm_campaign=icon
    // https://webstatic.mihoyo.com/app/community-game-records/?game_id=2&utm_source=bbs&utm_medium=mys&utm_campaign=box
    // https://webstatic.mihoyo.com/bbs/event/signin/hkrpg/index.html?bbs_auth_required=true&act_id=e202304121516551&bbs_auth_required=true&bbs_presentation_style=fullscreen&utm_source=bbs&utm_medium=mys&utm_campaign=icon
    // https://webstatic.mihoyo.com/app/community-game-records/rpg/index.html?mhy_presentation_style=fullscreen&game_id=6&utm_source=bbs&utm_medium=mys&utm_campaign=icon
    // https://webstatic.mihoyo.com/sr/event/rpg-srledger/index.html?mhy_game_role_required=hkrpg_cn&mhy_presentation_style=fullscreen&utm_source=bbs&utm_medium=mys&utm_campaign=icon




    /// <summary>
    /// 米游社账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public override async Task<GameRecordUser> GetGameRecordUserAsync(string cookie, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie));
        }
        var request = new HttpRequestMessage(HttpMethod.Get, "https://bbs-api.miyoushe.com/user/wapi/getUserFullInfo");
        request.Headers.Add(Cookie, cookie);
        request.Headers.Add(Referer, "https://www.miyoushe.com/");
        request.Headers.Add(DS, CreateSecret());
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_device_id, DeviceId);
        request.Headers.Add(x_rpc_client_type, "5");
        var data = await CommonSendAsync<GameRecordUserWrapper>(request, cancellationToken);
        data.User.Cookie = cookie;
        return data.User;
    }





    /// <summary>
    /// 所有游戏账号
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<List<GameRecordRole>> GetAllGameRolesAsync(string cookie, CancellationToken cancellationToken = default)
    {
        Lock @lock = new();
        var list = new List<GameRecordRole>();
        await Parallel.ForEachAsync([GameBiz.bh3_cn, GameBiz.hk4e_cn, GameBiz.hkrpg_cn, GameBiz.nap_cn], cancellationToken, async (GameBiz gameBiz, CancellationToken token) =>
        {
            var roles = await GetGameRolesAsync(cookie, gameBiz, token);
            if (roles.Count > 0)
            {
                lock (@lock)
                {
                    list.AddRange(roles);
                }
            }
        });
        return list;
    }



    /// <summary>
    /// 获取游戏账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="gameBiz"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<List<GameRecordRole>> GetGameRolesAsync(string cookie, GameBiz gameBiz, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie));
        }
        string url = $"https://passport-api.mihoyo.com/binding/api/getUserGameRolesByCookieToken?game_biz={gameBiz}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, cookie);
        //request.Headers.Add(DS, CreateSecret2(url));
        //request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        //request.Headers.Add(x_rpc_app_version, AppVersion);
        //request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
        var data = await CommonSendAsync<GameRecordRoleWrapper>(request, cancellationToken);
        if (data.List is not null)
        {
            foreach (var item in data.List)
            {
                item.Cookie = cookie;
                try
                {
                    item.HeadIcon = await GetGameRoleHeadIconAsync(item, cancellationToken);
                }
                catch (miHoYoApiException) { }
            }
        }
        return data.List ?? new List<GameRecordRole>();
    }



    /// <summary>
    /// 获取游戏账号头像
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected override async Task<string> GetGameRoleHeadIconAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = role.GameBiz switch
        {
            GameBiz.bh3_cn => $"https://api-takumi-record.mihoyo.com/game_record/app/honkai3rd/api/index?server={role.Region}&role_id={role.Uid}",
            GameBiz.hk4e_cn => $"https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/index?avatar_list_type=1&server={role.Region}&role_id={role.Uid}",
            GameBiz.hkrpg_cn => $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/index?server={role.Region}&role_id={role.Uid}",
            GameBiz.nap_cn => $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/index?server={role.Region}&role_id={role.Uid}",
            _ => throw new ArgumentOutOfRangeException($"Unsupport GameBiz: {role.GameBiz}"),
        };
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (role.GameBiz is GameBiz.nap_cn)
        {
            AddZZZGameRecordH5Headers(request, role, url);
        }
        else
        {
            AddGameRecordAppHeaders(request, role.Cookie, url);
        }
        var data = await CommonSendAsync<GameRecordIndex>(request, cancellationToken);
        return data.HeadIcon;
    }




    /// <summary>
    /// 获取设备指纹信息
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<string> GetDeviceFpAsync(CancellationToken cancellationToken = default)
    {
        const string url = "https://public-data-api.mihoyo.com/device-fp/api/getFp";
        // device_id / seed_id / seed_time 都要跨刷新稳定复用：每次换值，服务端就当成换了一台设备。
        if (string.IsNullOrWhiteSpace(AndroidId))
        {
            AndroidId = GenerateAndroidId();
        }
        if (string.IsNullOrWhiteSpace(DeviceFpSeedId))
        {
            DeviceFpSeedId = Guid.NewGuid().ToString("D");
        }
        if (string.IsNullOrWhiteSpace(DeviceFpSeedTime))
        {
            DeviceFpSeedTime = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
        }
        // ext_fields 是「装着 JSON 的字符串」，所以要序列化两次：
        // 先把机型字段序列化成 JSON 文本，再把这个文本当成字符串值序列化进 body。
        // 只序列化一次会把 ext_fields 变成 JSON 对象，服务端直接 -502。
        string extFields = JsonSerializer.Serialize(BuildDeviceExtFields());
        string postContent = $$"""
            {
                "device_id": "{{AndroidId}}",
                "seed_id": "{{DeviceFpSeedId}}",
                "seed_time": "{{DeviceFpSeedTime}}",
                "platform": "2",
                "device_fp": "{{DeviceFp}}",
                "app_name": "bbs_cn",
                "ext_fields": {{JsonSerializer.Serialize(extFields)}},
                "bbs_device_id": "{{DeviceId}}"
            }
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(postContent),
        };
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        // 这两个头要和 body 里的设备信息一致，不能各说各话。
        request.Headers.TryAddWithoutValidation("x-rpc-device_name", Uri.EscapeDataString(DeviceName));
        request.Headers.TryAddWithoutValidation("x-rpc-sys_version", SystemVersion);
        // B 组：带上账号 Cookie 申请，服务端才会把这枚指纹登记到该账号上（见 GameRecordClient.GetDeviceFpCookie）。
        // 没有账号 Cookie 时照旧匿名申请，不影响申请本身能否成功。
        if (!string.IsNullOrWhiteSpace(GetDeviceFpCookie))
        {
            request.Headers.TryAddWithoutValidation(Cookie, GetDeviceFpCookie);
        }
        var data = await CommonSendAsync<DeviceFpResult>(request, cancellationToken);
        if (data.Code != 200)
        {
            throw new miHoYoApiException(data.Code, data.Message);
        }
        DeviceFp = data.DeviceFp;
        return data.DeviceFp;
    }




    /// <summary>16 位十六进制随机串。真机上的 ANDROID_ID 就是这个形状。</summary>
    public static string GenerateAndroidId()
    {
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLower();
    }



    /// <summary>
    /// 组装 getFp 的 <c>ext_fields</c>。
    /// <para>
    /// 服务端会拿这些字段判断设备是否可信。之前这里固定写着 <c>hardware=windows_x86_64</c>、
    /// <c>board=windows</c>、<c>debugStatus=1</c>，等于自称一台 Windows 模拟器，
    /// 设备指纹因此被标记，绝区零便笺、绳网月报这些接口直接返回 10041（账号存在风险）。
    /// 现在整份载荷由一台仓库自带的公开机型（Pixel 5）推导，并且与 UA、<c>x-rpc-device_name</c> 保持自洽。
    /// </para>
    /// </summary>
    private object BuildDeviceExtFields()
    {
        HyperionDeviceProfile profile = DeviceProfile;
        long nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        string deviceInfo = $"{profile.Brand}/{profile.ProductName}/{profile.ProductName}:{SystemVersion}/{profile.BuildId}/{profile.Incremental}:user/release-keys";
        return new
        {
            proxyStatus = 0,
            isRoot = 0,
            romCapacity = "128",
            deviceName = DeviceName,
            productName = profile.ProductName,
            romRemain = "64",
            hostname = profile.ProductName,
            screenSize = "1080x2400",
            isTablet = 0,
            aaid = "",
            model = DeviceName,
            brand = profile.Brand,
            hardware = profile.Hardware,
            deviceType = profile.ProductName,
            devId = "REL",
            serialNumber = "unknown",
            sdCapacity = 128000,
            buildTime = profile.BuildTime.ToString(),
            buildUser = "android-build",
            simState = 1,
            ramRemain = "4096",
            appUpdateTimeDiff = nowMs - 172800000,
            deviceInfo,
            vaid = "",
            buildType = "user",
            sdkVersion = GetApiLevel(SystemVersion).ToString(),
            ui_mode = "UI_MODE_TYPE_NORMAL",
            isMockLocation = 0,
            cpuType = "arm64-v8a",
            isAirMode = 0,
            ringMode = 2,
            chargeStatus = 3,
            manufacturer = profile.Manufacturer,
            emulatorStatus = 0,
            appMemory = "512",
            osVersion = SystemVersion,
            vendor = "unknown",
            accelerometer = "0.12x9.81x0.35",
            sdRemain = 96000,
            buildTags = "release-keys",
            packageName = "com.mihoyo.hyperion",
            networkType = "WiFi",
            oaid = "",
            debugStatus = 0,
            ramCapacity = "8192",
            magnetometer = "-12.4x3.1x41.7",
            display = profile.BuildId,
            appInstallTimeDiff = nowMs - 2592000000,
            packageVersion = AppVersion,
            gyroscope = "0.001x-0.002x0.0",
            batteryStatus = 82,
            hasKeyboard = 0,
            board = profile.ProductName,
        };
    }



    /// <summary>
    /// Android 版本号对应的 API level，用于 ext_fields 的 <c>sdkVersion</c>。
    /// 该字段是数字，之前直接填了版本号字符串。无法识别时按最高的 API level（36，Android 16）处理。
    /// </summary>
    private static int GetApiLevel(string systemVersion) => systemVersion switch
    {
        "8.0" => 26,
        "9" => 28,
        "10" => 29,
        "11" => 30,
        "12" => 31,
        "12L" => 32,
        "13" => 33,
        "14" => 34,
        "15" => 35,
        _ => 36,
    };





    #region BH3


    /// <summary>
    /// 崩坏3实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<BH3DailyNote> GetBH3DailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://act-api-takumi.mihoyo.com/game_record/appv2/honkai3rd/api/note?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url, "https://act.mihoyo.com/");
        return await CommonSendAsync<BH3DailyNote>(request, cancellationToken);
    }



    #endregion





    #region Genshin


    /// <summary>
    /// 获取原神账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<List<GameRecordRole>> GetGenshinGameRolesAsync(string cookie, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie));
        }
        var url = "https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=hk4e_cn";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, cookie);
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        var data = await CommonSendAsync<GameRecordRoleWrapper>(request, cancellationToken);
        data.List?.ForEach(x => x.Cookie = cookie);
        return data.List ?? new List<GameRecordRole>();
    }


    /// <summary>
    /// 深境螺旋
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<SpiralAbyssInfo> GetSpiralAbyssInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/spiralAbyss?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        var data = await CommonSendAsync<SpiralAbyssInfo>(request, cancellationToken);
        data.Uid = role.Uid;
        return data;
    }


    /// <summary>
    /// 旅行札记总览
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">0 当前月</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>

    public override async Task<TravelersDiarySummary> GetTravelsDiarySummaryAsync(GameRecordRole role, int month = 0, CancellationToken cancellationToken = default)
    {
        var url = $"https://hk4e-api.mihoyo.com/event/ys_ledger/monthInfo?month={month}&bind_uid={role.Uid}&bind_region={role.Region}&bbs_presentation_style=fullscreen&bbs_auth_required=true&utm_source=bbs&utm_medium=mys&utm_campaign=icon";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        return await CommonSendAsync<TravelersDiarySummary>(request, cancellationToken);
    }


    /// <summary>
    /// 旅行札记收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month"></param>
    /// <param name="type">1原石，2摩拉</param>
    /// <param name="page">从1开始</param>
    /// <param name="limit">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回一页收入记录</returns>
    public override async Task<TravelersDiaryDetail> GetTravelsDiaryDetailByPageAsync(GameRecordRole role, int month, int type, int page, int limit = 100, CancellationToken cancellationToken = default)
    {
        var url = $"https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?page={page}&month={month}&limit={limit}&type={type}&bind_uid={role.Uid}&bind_region={role.Region}&bbs_presentation_style=fullscreen&bbs_auth_required=true&utm_source=bbs&utm_medium=mys&utm_campaign=icon";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        var data = await CommonSendAsync<TravelersDiaryDetail>(request, cancellationToken);
        foreach (var item in data.List)
        {
            item.Type = type;
        }
        return data;
    }


    /// <summary>
    /// 旅行札记收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month"></param>
    /// <param name="type">1原石，2摩拉</param>
    /// <param name="limit">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回该月所有收入记录</returns>
    public override async Task<TravelersDiaryDetail> GetTravelsDiaryDetailAsync(GameRecordRole role, int month, int type, int limit = 100, CancellationToken cancellationToken = default)
    {
        var data = await GetTravelsDiaryDetailByPageAsync(role, month, type, 1, limit, cancellationToken);
        if (data.List.Count < limit)
        {
            return data;
        }
        for (int i = 2; ; i++)
        {
            var addData = await GetTravelsDiaryDetailByPageAsync(role, month, type, i, limit, cancellationToken);
            data.List.AddRange(addData.List);
            if (addData.List.Count < limit)
            {
                break;
            }
        }
        return data;
    }



    /// <summary>
    /// 幻想真境剧诗
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<List<ImaginariumTheaterInfo>> GetImaginariumTheaterInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/role_combat?server={role.Region}&role_id={role.Uid}&active=1&need_detail=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(DS, CreateSecret2(url));
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(x_rpc_device_id, DeviceId);
        request.Headers.Add(x_rpc_device_fp, DeviceFp);
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        var warpper = await CommonSendAsync<ImaginariumTheaterWarpper>(request, cancellationToken);
        foreach (var item in warpper.Data)
        {
            item.Uid = role.Uid;
            item.ScheduleId = item.Schedule.ScheduleId;
            item.StartTime = item.Schedule.StartDateTime;
            item.EndTime = item.Schedule.EndDateTime;
            item.DifficultyId = item.Stat.DifficultyId;
            item.MaxRoundId = item.Stat.MaxRoundId + item.Stat.TarotFinishedCnt;
            item.Heraldry = item.Stat.Heraldry;
            item.MedalNum = item.Stat.GetMedalRoundList.Count(x => x == 1);
        }
        return warpper.Data;
    }


    /// <summary>
    /// 幽境危战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<List<StygianOnslaughtInfo>> GetStygianOnslaughtInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/hard_challenge?server={role.Region}&role_id={role.Uid}&need_detail=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        var warpper = await CommonSendAsync<StygianOnslaughtWrapper>(request, cancellationToken);
        foreach (var item in warpper.Data)
        {
            item.Uid = role.Uid;
            item.ScheduleId = item.Schedule.ScheduleId;
            item.StartDateTime = item.Schedule.StartDateTime;
            item.EndDateTime = item.Schedule.EndDateTime;
            item.Difficulty = item.SinglePlayer.Best?.Difficulty ?? 0;
            item.Second = item.SinglePlayer.Best?.Seconds ?? 0;
        }
        return warpper.Data;
    }



    /// <summary>
    /// 原神每日便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<GenshinDailyNote> GetGenshinDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/dailyNote?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        return await CommonSendAsync<GenshinDailyNote>(request, cancellationToken);
    }



    #endregion




    #region StarRail


    /// <summary>
    /// 获取星穹铁道账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException">输入的 <c>cookie</c> 为空</exception>
    public override async Task<List<GameRecordRole>> GetStarRailGameRolesAsync(string cookie, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie));
        }
        const string url = "https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=hkrpg_cn";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, cookie);
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        var data = await CommonSendAsync<GameRecordRoleWrapper>(request, cancellationToken);
        data.List?.ForEach(x => x.Cookie = cookie);
        return data.List ?? new List<GameRecordRole>();
    }


    /// <summary>
    /// 忘却之庭
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ForgottenHallInfo> GetForgottenHallInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/challenge?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}&need_all=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        var data = await CommonSendAsync<ForgottenHallInfo>(request, cancellationToken);
        data.Uid = role.Uid;
        return data;
    }


    /// <summary>
    /// 虚构叙事
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<PureFictionInfo> GetPureFictionInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/challenge_story?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}&isPrev=1&need_all=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        var data = await CommonSendAsync<PureFictionInfo>(request, cancellationToken);
        data.Uid = role.Uid;
        if (data.Metas?.Count > 0)
        {
            if (schedule == 1)
            {
                data.ScheduleId = data.Metas[0].ScheduleId;
                data.BeginTime = data.Metas[0].BeginTime;
                data.EndTime = data.Metas[0].EndTime;
            }
            if (schedule == 2 && data.Metas.Count > 1)
            {
                data.ScheduleId = data.Metas[1].ScheduleId;
                data.BeginTime = data.Metas[1].BeginTime;
                data.EndTime = data.Metas[1].EndTime;
            }
        }
        return data;
    }


    /// <summary>
    /// 末日幻影
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ApocalypticShadowInfo> GetApocalypticShadowInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/challenge_boss?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}&isPrev=1&need_all=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        var data = await CommonSendAsync<ApocalypticShadowInfo>(request, cancellationToken);
        data.Uid = role.Uid;
        if (data.Metas?.Count > 0)
        {
            if (schedule == 1)
            {
                data.ScheduleId = data.Metas[0].ScheduleId;
                data.BeginTime = data.Metas[0].BeginTime;
                data.EndTime = data.Metas[0].EndTime;
                data.UpperBossIcon = data.Metas[0].UpperBoss.Icon;
                data.LowerBossIcon = data.Metas[0].LowerBoss.Icon;
                data.TierceBossIcon = data.Metas[0].TierceBoss?.Icon;
            }
            if (schedule == 2 && data.Metas.Count > 1)
            {
                data.ScheduleId = data.Metas[1].ScheduleId;
                data.BeginTime = data.Metas[1].BeginTime;
                data.EndTime = data.Metas[1].EndTime;
                data.UpperBossIcon = data.Metas[1].UpperBoss.Icon;
                data.LowerBossIcon = data.Metas[1].LowerBoss.Icon;
                data.TierceBossIcon = data.Metas[1].TierceBoss?.Icon;
            }
        }
        return data;
    }


    /// <summary>
    /// 模拟宇宙
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<SimulatedUniverseInfo> GetSimulatedUniverseInfoAsync(GameRecordRole role, bool detail = false, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/rogue?role_id={role.Uid}&server={role.Region}&schedule_type=3&need_detail={detail.ToString().ToLower()}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        var data = await CommonSendAsync<SimulatedUniverseInfo>(request, cancellationToken);
        return data;
    }


    /// <summary>
    /// 开拓月历总结
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">还不清楚规律，可能是 202304</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<TrailblazeCalendarSummary> GetTrailblazeCalendarSummaryAsync(GameRecordRole role, string month = "", CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi.mihoyo.com/event/srledger/month_info?uid={role.Uid}&region={role.Region}&month={month}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        return await CommonSendAsync<TrailblazeCalendarSummary>(request, cancellationToken);
    }


    /// <summary>
    /// 开拓月历收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202304</param>
    /// <param name="type">1 星琼 2 星轨票</param>
    /// <param name="page">从1开始</param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回一页收入记录</returns>
    public override async Task<TrailblazeCalendarDetail> GetTrailblazeCalendarDetailByPageAsync(GameRecordRole role, string month, int type, int page, int page_size = 100, CancellationToken cancellationToken = default)
    {
        // 
        var url = $"https://api-takumi.mihoyo.com/event/srledger/month_detail?uid={role.Uid}&region={role.Region}&month={month}&type={type}&current_page={page}&page_size={page_size}&total=0";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        var data = await CommonSendAsync<TrailblazeCalendarDetail>(request, cancellationToken);
        foreach (var item in data.List)
        {
            item.Type = type;
        }
        return data;
    }


    /// <summary>
    /// 开拓月历收入详情
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202304</param>
    /// <param name="type">1 星琼 2 星轨票</param>
    /// <param name="page_size">最大100</param>
    /// <param name="cancellationToken"></param>
    /// <returns>返回该月所有收入记录</returns>
    public override async Task<TrailblazeCalendarDetail> GetTrailblazeCalendarDetailAsync(GameRecordRole role, string month, int type, int page_size = 100, CancellationToken cancellationToken = default)
    {
        page_size = Math.Clamp(page_size, 20, 100);
        var data = await GetTrailblazeCalendarDetailByPageAsync(role, month, type, 1, page_size, cancellationToken);
        if (data.List.Count < page_size)
        {
            return data;
        }
        for (int i = 2; ; i++)
        {
            var addData = await GetTrailblazeCalendarDetailByPageAsync(role, month, type, i, page_size, cancellationToken);
            data.List.AddRange(addData.List);
            if (addData.List.Count < page_size)
            {
                break;
            }
        }
        return data;
    }



    /// <summary>
    /// 星穹铁道实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<StarRailDailyNote> GetStarRailDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/note?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        return await CommonSendAsync<StarRailDailyNote>(request, cancellationToken);
    }


    /// <summary>
    /// 星穹铁道异相仲裁
    /// </summary>
    /// <param name="role"></param>
    /// <param name="scheduleType">1 当期，3 最近三期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ChallengePeakData> GetStarRailChallengePeakDataAsync(GameRecordRole role, int scheduleType, CancellationToken cancellationToken = default)
    {
        string url = $"https://api-takumi-record.mihoyo.com/game_record/app/hkrpg/api/challenge_peak?server={role.Region}&role_id={role.Uid}&schedule_type={scheduleType}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddGameRecordAppHeaders(request, role.Cookie, url);
        return await CommonSendAsync<ChallengePeakData>(request, cancellationToken);
    }



    #endregion




    #region ZZZ



    /// <summary>
    /// 获取绝区零账号信息
    /// </summary>
    /// <param name="cookie"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<List<GameRecordRole>> GetZZZGameRolesAsync(string cookie, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentNullException(nameof(cookie));
        }
        var url = "https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=nap_cn";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, cookie);
        request.Headers.Add(X_Request_With, com_mihoyo_hyperion);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        var data = await CommonSendAsync<GameRecordRoleWrapper>(request, cancellationToken);
        data.List?.ForEach(x => x.Cookie = cookie);
        return data.List ?? new List<GameRecordRole>();
    }


    /// <summary>
    /// 绝区零抽卡记录
    /// </summary>
    /// <param name="role"></param>
    /// <param name="gachaType"></param>
    /// <param name="endId">首次请求不传</param>
    /// <param name="language"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ZZZGachaRecordData> GetZZZGachaRecordAsync(GameRecordRole role, int gachaType, long? endId = null, string? language = null, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/gacha_record?uid={role.Uid}&region={role.Region}&gacha_type={gachaType}";
        long validEndId = endId.GetValueOrDefault();
        if (validEndId > 0)
        {
            url += $"&end_id={validEndId}";
        }
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<ZZZGachaRecordData>(request, cancellationToken);
    }


    /// <summary>
    /// 式舆防卫战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ShiyuDefenseWrapper> GetShiyuDefenseInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/hadal_info_v2?schedule_type={schedule}&server={role.Region}&role_id={role.Uid}&need_all=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<ShiyuDefenseWrapper>(request, cancellationToken);
    }


    /// <summary>
    /// 危局强袭战
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<DeadlyAssaultInfo> GetDeadlyAssaultInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/hadal_mem_detail_v2?schedule_type={schedule}&region={role.Region}&uid={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<DeadlyAssaultInfo>(request, cancellationToken);
    }


    /// <summary>
    /// 绳网月报总结
    /// </summary>
    /// <param name="role"></param>
    /// <param name="month">202409</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<InterKnotReportSummary> GetInterKnotReportSummaryAsync(GameRecordRole role, string month = "", CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi.mihoyo.com/event/nap_ledger/month_info?uid={role.Uid}&region={role.Region}&month={month}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<InterKnotReportSummary>(request, cancellationToken);
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
    public override async Task<InterKnotReportDetail> GetInterKnotReportDetailByPageAsync(GameRecordRole role, string month, string type, int page, int page_size = 100, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi.mihoyo.com/event/nap_ledger/month_detail?uid={role.Uid}&region={role.Region}&month={month}&type={type}&current_page={page}&page_size={page_size}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<InterKnotReportDetail>(request, cancellationToken);
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
    public override async Task<InterKnotReportDetail> GetInterKnotReportDetailAsync(GameRecordRole role, string month, string type, int page_size = 100, CancellationToken cancellationToken = default)
    {
        page_size = Math.Clamp(page_size, 20, 100);
        var data = await GetInterKnotReportDetailByPageAsync(role, month, type, 1, page_size, cancellationToken);
        if (data.List.Count < page_size)
        {
            return data;
        }
        for (int i = 2; ; i++)
        {
            var addData = await GetInterKnotReportDetailByPageAsync(role, month, type, i, page_size, cancellationToken);
            data.List.AddRange(addData.List);
            if (addData.List.Count < page_size)
            {
                break;
            }
        }
        return data;
    }



    /// <summary>
    /// 养成指南，不可用，返回未登录错误
    /// </summary>
    /// <param name="role"></param>
    /// <param name="avatar_id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [Obsolete("不可用，返回未登录错误", true)]
    public override async Task<UpgradeGuideItemList> GetZZZUpgradeGuideItemListAsync(GameRecordRole role, int avatar_id = 1011, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi.mihoyo.com/event/nap_cultivate_tool/user/item_list?uid={role.Uid}&region={role.Region}&avatar_id={avatar_id}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_device_id, DeviceId);
        request.Headers.Add(x_rpc_device_fp, DeviceFp);
        return await CommonSendAsync<UpgradeGuideItemList>(request, cancellationToken);
    }



    /// <summary>
    /// 养成指南，不可用，返回未登录错误
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [Obsolete("不可用，返回未登录错误", true)]
    public override async Task<UpgradeGuidIconInfo> GetZZZUpgradeGuideIconInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var url = $"https://api-takumi.mihoyo.com/event/nap_cultivate_tool/user/icon_info?uid={role.Uid}&region={role.Region}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Cookie, role.Cookie);
        request.Headers.Add(Referer, "https://act.mihoyo.com/");
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_device_id, DeviceId);
        request.Headers.Add(x_rpc_device_fp, DeviceFp);
        return await CommonSendAsync<UpgradeGuidIconInfo>(request, cancellationToken);
    }



    /// <summary>
    /// 绝区零实时便笺
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ZZZDailyNote> GetZZZDailyNoteAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/note?server={role.Region}&role_id={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<ZZZDailyNote>(request, cancellationToken);
    }



    /// <summary>
    /// 绝区零临界推演
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ThresholdSimulationAbstractInfo> GetZZZThresholdSimulationAbstractInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/void_front_battle_abstract_info?region={role.Region}&uid={role.Uid}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<ThresholdSimulationAbstractInfo>(request, cancellationToken);
    }



    /// <summary>
    /// 绝区零临界推演
    /// </summary>
    /// <param name="role"></param>
    /// <param name="void_front_id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<ThresholdSimulationDetailInfo> GetZZZThresholdSimulationDetailInfoAsync(GameRecordRole role, int void_front_id, CancellationToken cancellationToken = default)
    {
        string url = $"https://api-takumi-record.mihoyo.com/event/game_record_zzz/api/zzz/void_front_battle_detail?region={role.Region}&uid={role.Uid}&void_front_id={void_front_id}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddZZZGameRecordH5Headers(request, role, url);
        return await CommonSendAsync<ThresholdSimulationDetailInfo>(request, cancellationToken);
    }



    #endregion


}
