using System.Net.Http;

namespace Starward.Core.GameRecord.SignIn;

/// <summary>
/// 国服（米游社）每日签到客户端。
/// </summary>
public class HyperionSignInClient : SignInClientBase
{

    public override string AppVersion => HyperionAppVersion;

    public override string DefaultUAContent => HyperionUAContent;

    public override ServerRegion ServerRegion => ServerRegion.miHoYo;


    public HyperionSignInClient(HttpClient? httpClient = null) : base(httpClient)
    {

    }


    /// <summary>
    /// 国服签到请求头：Referer 用 webstatic，带 <c>x-rpc-signgame</c> 区分游戏；
    /// 绝区零活动主机需要它自己的 Origin，其余游戏用默认 act.mihoyo.com。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="config">签到活动配置，提供 signgame / origin。</param>
    /// <param name="signData">是否为数据接口（保留参数，当前 home 与数据接口共用相同请求头）。</param>
    protected override void AddSignInPlatformHeaders(HttpRequestMessage request, SignInActivityConfig config, bool signData)
    {
        request.Headers.Add(Referer, "https://webstatic.mihoyo.com/");
        request.Headers.Add("x-rpc-signgame", config.SignGame);
        request.Headers.TryAddWithoutValidation("Origin", string.IsNullOrEmpty(config.Origin) ? "https://act.mihoyo.com" : config.Origin);
        request.Headers.TryAddWithoutValidation(X_Request_With, com_mihoyo_hyperion);
        request.Headers.Add(x_rpc_app_version, AppVersion);
        request.Headers.Add(x_rpc_client_type, "5");
    }

}
