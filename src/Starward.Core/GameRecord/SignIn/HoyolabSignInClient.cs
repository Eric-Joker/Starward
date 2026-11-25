using System.Net.Http;

namespace Starward.Core.GameRecord.SignIn;

/// <summary>
/// 国际服（HoYoLAB）每日签到客户端。
/// </summary>
public class HoyolabSignInClient : SignInClientBase
{

    public override string AppVersion => HoyolabAppVersion;

    public override string DefaultUAContent => HoyolabUAContent;

    public override ServerRegion ServerRegion => ServerRegion.HoYoverse;


    public HoyolabSignInClient(HttpClient? httpClient = null) : base(httpClient)
    {

    }


    /// <summary>
    /// 国际服签到请求头：Referer 用 act.hoyolab.com，带 <c>x-rpc-signgame</c> 区分游戏；
    /// 绝区零活动主机需要它自己的 Origin，其余游戏用默认 act.hoyolab.com。
    /// 语言头（x-rpc-lang / x-rpc-language）由基类按 <see cref="GameRecordClientBase.Language"/> 统一补。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="config">签到活动配置（提供 signgame / origin）。</param>
    /// <param name="signData">保留参数（OS 侧 home 与数据接口共用相同请求头）。</param>
    protected override void AddSignInPlatformHeaders(HttpRequestMessage request, SignInActivityConfig config, bool signData)
    {
        request.Headers.Add(Referer, "https://act.hoyolab.com/");
        request.Headers.Add("x-rpc-signgame", config.SignGame);
        request.Headers.TryAddWithoutValidation("Origin", string.IsNullOrEmpty(config.Origin) ? "https://act.hoyolab.com" : config.Origin);
        request.Headers.TryAddWithoutValidation(X_Request_With, com_mihoyo_hoyolab);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
    }

}
