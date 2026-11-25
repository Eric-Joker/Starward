using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Starward.Core.GameRecord.SignIn;

/// <summary>
/// 每日签到（米游社 luna / HoYoLAB sol）请求的公共实现。
/// <para>
/// 签到接口本身与游戏无关，只有活动 id、主机和区服相关请求头不同，因此单独抽一对
/// <see cref="HyperionSignInClient"/> / <see cref="HoyolabSignInClient"/>，
/// 而不把签到方法塞进每个游戏各自 Client + Interface 里。
/// </para>
/// </summary>
public abstract class SignInClientBase : GameRecordClientBase
{

    /// <summary>
    /// 签到接口 URL 里的语言参数（CN 固定 zh-cn；国际服的语言由 HoYolab 客户端的语言头决定）。
    /// </summary>
    protected virtual string SignInLanguage => "zh-cn";


    protected SignInClientBase(HttpClient? httpClient = null) : base(httpClient)
    {

    }


    /// <summary>
    /// 获取当前角色对应的签到活动配置，不支持的游戏抛出异常。
    /// </summary>
    /// <param name="role">游戏角色，用于解析 <see cref="GameRecordRole.GameBiz"/>。</param>
    /// <returns>该游戏 + 区服对应的签到活动配置。</returns>
    /// <exception cref="miHoYoApiException">当前游戏不支持签到时抛出。</exception>
    protected static SignInActivityConfig GetSignInConfigOrThrow(GameRecordRole role)
    {
        GameBiz biz = role.GameBiz;
        SignInActivityConfig? config = SignInActivityConfig.FromGame(biz.Game, biz.Server is "global");
        if (config is null)
        {
            throw new miHoYoApiException(-1, $"Sign-in is not supported for game biz: {role.GameBiz}");
        }
        return config;
    }


    /// <summary>
    /// 为签到请求添加平台相关请求头（signgame / Origin 等）。
    /// </summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="config">签到活动配置，提供 signgame / origin 等字段。</param>
    /// <param name="signData">是否为数据接口（home 为 false；info/sign/resign 为 true）。</param>
    protected abstract void AddSignInPlatformHeaders(HttpRequestMessage request, SignInActivityConfig config, bool signData);


    /// <summary>
    /// 本月签到奖励列表（home 接口）。
    /// </summary>
    /// <param name="role">游戏角色，提供 cookie / region / uid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当月每日奖励列表。</returns>
    public async Task<SignInReward> GetSignInRewardAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var request = new HttpRequestMessage(HttpMethod.Get, config.HomeUrl(SignInLanguage));
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: false);
        return await CommonSendAsync<SignInReward>(request, cancellationToken);
    }


    /// <summary>
    /// 当前签到状态（已签天数、今日是否已签等，info 接口）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到状态信息。</returns>
    public async Task<SignInRewardInfo> GetSignInInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var request = new HttpRequestMessage(HttpMethod.Get, config.InfoUrl(SignInLanguage, role.Region, role.Uid));
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: true);
        return await CommonSendAsync<SignInRewardInfo>(request, cancellationToken);
    }


    /// <summary>
    /// 补签信息（剩余补签次数、消耗货币等，resign_info 接口）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签配额与货币信息。</returns>
    public async Task<SignInResignInfo> GetSignInResignInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var request = new HttpRequestMessage(HttpMethod.Get, config.ResignInfoUrl(SignInLanguage, role.Region, role.Uid));
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: true);
        return await CommonSendAsync<SignInResignInfo>(request, cancellationToken);
    }


    /// <summary>
    /// 执行今日签到，成功返回 retcode 0；今日已签返回 -5003（由上层处理）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到结果，含风控字段。</returns>
    public async Task<SignInResult> SignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        return await PostSignInAsync(role, resign: false, cancellationToken);
    }


    /// <summary>
    /// 执行补签。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签结果，含风控字段。</returns>
    public async Task<SignInResult> ReSignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        return await PostSignInAsync(role, resign: true, cancellationToken);
    }


    /// <summary>
    /// 签到 / 补签的 POST 公共实现。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="resign">true 走补签接口，false 走签到接口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>接口返回的签到结果。</returns>
    private async Task<SignInResult> PostSignInAsync(GameRecordRole role, bool resign, CancellationToken cancellationToken)
    {
        SignInActivityConfig config = GetSignInConfigOrThrow(role);
        var body = new SignInPostBody(config.ActId, role.Region, role.Uid.ToString());
        string json = JsonSerializer.Serialize(body, typeof(SignInPostBody), GameRecordJsonContext.Default);
        string url = resign ? config.ResignUrl() : config.SignUrl();
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, application_json),
        };
        request.Headers.Add(Cookie, role.Cookie);
        AddSignInPlatformHeaders(request, config, signData: true);
        return await CommonSendAsync<SignInResult>(request, cancellationToken);
    }

}
