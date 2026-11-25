using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.SignIn;
using Starward.Features.Database;
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord.SignIn;

/// <summary>
/// 每日签到的业务编排：拉取状态、签到、补签，并把接口返回码映射成结构化结果。
/// <para>
/// 0.19 的签到客户端按区服分成 <see cref="HyperionSignInClient"/> / <see cref="HoyolabSignInClient"/>，
/// 设备指纹复用 <see cref="GameRecordRoleService"/> 已有的申请 / 轮换策略，因此这里只负责
/// 「按角色挑客户端 + 补 Cookie + 把结果翻译成 UI 能消费的枚举」。
/// </para>
/// </summary>
internal class SignInService
{

    private readonly ILogger<SignInService> _logger;

    private readonly GameRecordRoleService _gameRecordRoleService;

    private readonly HyperionSignInClient _hyperionSignInClient;

    private readonly HoyolabSignInClient _hoyolabSignInClient;

    private readonly IMemoryCache _memoryCache;


    /// <summary>
    /// 初始化签到业务服务。
    /// </summary>
    /// <param name="logger">日志。</param>
    /// <param name="gameRecordRoleService">只用于复用设备指纹申请与角色 Cookie 刷新。</param>
    /// <param name="hyperionSignInClient">国服（米游社）签到 Client。</param>
    /// <param name="hoyolabSignInClient">国际服（HoYoLAB）签到 Client。</param>
    /// <param name="memoryCache">奖励列表内存缓存。</param>
    public SignInService(
        ILogger<SignInService> logger,
        GameRecordRoleService gameRecordRoleService,
        HyperionSignInClient hyperionSignInClient,
        HoyolabSignInClient hoyolabSignInClient,
        IMemoryCache memoryCache)
    {
        _logger = logger;
        _gameRecordRoleService = gameRecordRoleService;
        _hyperionSignInClient = hyperionSignInClient;
        _hoyolabSignInClient = hoyolabSignInClient;
        _memoryCache = memoryCache;
    }



    /// <summary>
    /// 拉取签到卡片所需的全部状态（奖励列表 + 当前签到状态 + 补签信息）。
    /// 奖励列表按「游戏 + 服务器年月」缓存在内存与 KVT，跨月自动失效；补签信息可选（失败不影响主流程）。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到卡片展示所需的聚合状态。</returns>
    public async Task<SignInStatus> GetSignInStatusAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        role = await EnsureCookieAsync(role, cancellationToken);
        SignInClientBase client = await PrepareClientAsync(role, cancellationToken);

        SignInRewardInfo info = await client.GetSignInInfoAsync(role, cancellationToken);

        SignInReward reward = await GetSignInRewardCachedAsync(client, role, info, cancellationToken);

        SignInResignInfo? resignInfo = null;
        try
        {
            resignInfo = await client.GetSignInResignInfoAsync(role, cancellationToken);
        }
        catch (miHoYoApiException ex)
        {
            // 部分游戏 / 活动没有补签信息，忽略
            _logger.LogDebug(ex, "Get resign info failed (biz {biz}, uid {uid})", role.GameBiz, role.Uid);
        }

        return new SignInStatus
        {
            Reward = reward,
            Info = info,
            ResignInfo = resignInfo,
        };
    }



    /// <summary>
    /// 只取签到状态（已签天数、今日是否已签、服务器日期）。自动签到用它判断今天要不要签。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到状态。</returns>
    public async Task<SignInRewardInfo> GetSignInInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        role = await EnsureCookieAsync(role, cancellationToken);
        SignInClientBase client = await PrepareClientAsync(role, cancellationToken);
        return await client.GetSignInInfoAsync(role, cancellationToken);
    }



    /// <summary>
    /// 执行今日签到，返回结构化结果。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到操作结果（含已签、风控、Cookie 失效等）。</returns>
    public async Task<SignInActionResponse> ClaimSignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        try
        {
            role = await EnsureCookieAsync(role, cancellationToken);
            SignInClientBase client = await PrepareClientAsync(role, cancellationToken);
            SignInResult result = await client.SignInAsync(role, cancellationToken);
            if (IsRiskControl(result))
            {
                // 风控响应有多个字段组合，不能只依赖 gt；部分活动只返回 risk_code 或 is_risk。
                return new SignInActionResponse(SignInActionResult.RiskControl, null, null);
            }
            return new SignInActionResponse(SignInActionResult.Success, SignInReturnCode.Success, null);
        }
        catch (miHoYoApiException ex)
        {
            if (SignInReturnCode.IsLoginExpired(ex.ReturnCode))
            {
                return new SignInActionResponse(SignInActionResult.CookieExpired, ex.ReturnCode, ex.Message);
            }
            if (SignInReturnCode.IsRiskControl(ex.ReturnCode))
            {
                return new SignInActionResponse(SignInActionResult.RiskControl, ex.ReturnCode, ex.Message);
            }
            return new SignInActionResponse(MapReturnCode(ex.ReturnCode), ex.ReturnCode, ex.Message);
        }
    }



    /// <summary>
    /// 执行补签，返回结构化结果。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签操作结果。</returns>
    public async Task<SignInActionResponse> ClaimReSignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        try
        {
            role = await EnsureCookieAsync(role, cancellationToken);
            SignInClientBase client = await PrepareClientAsync(role, cancellationToken);
            SignInResult result = await client.ReSignInAsync(role, cancellationToken);
            if (IsRiskControl(result))
            {
                return new SignInActionResponse(SignInActionResult.RiskControl, null, null);
            }
            return new SignInActionResponse(SignInActionResult.Success, SignInReturnCode.Success, null);
        }
        catch (miHoYoApiException ex)
        {
            if (SignInReturnCode.IsLoginExpired(ex.ReturnCode))
            {
                return new SignInActionResponse(SignInActionResult.CookieExpired, ex.ReturnCode, ex.Message);
            }
            if (SignInReturnCode.IsRiskControl(ex.ReturnCode))
            {
                return new SignInActionResponse(SignInActionResult.RiskControl, ex.ReturnCode, ex.Message);
            }
            return new SignInActionResponse(MapReturnCode(ex.ReturnCode), ex.ReturnCode, ex.Message);
        }
    }



    /// <summary>
    /// 按角色选 Client，并把 0.19 统一管理的设备指纹与界面语言写进客户端。
    /// <para>
    /// 签到与战绩共用同一套设备指纹：这里只从 <see cref="GameRecordRoleService.GetDeviceInfoAsync"/>
    /// 取现成的，不自己再申请一份，否则同一台机器会出现两枚互相矛盾的指纹。
    /// </para>
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该角色应使用的 Client。</returns>
    private async Task<SignInClientBase> PrepareClientAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        ServerRegion region = SignInGameSupport.IsGlobalServerRole(role) ? ServerRegion.HoYoverse : ServerRegion.miHoYo;
        SignInClientBase client = region is ServerRegion.HoYoverse ? _hoyolabSignInClient : _hyperionSignInClient;
        try
        {
            client.DeviceInfo = await _gameRecordRoleService.GetDeviceInfoAsync(region, cancellationToken);
        }
        catch (Exception ex)
        {
            // 没有指纹也能试一次签到，别因为申请指纹失败就整轮失败
            _logger.LogDebug(ex, "Update device fp before sign-in failed.");
        }
        client.Language = CultureInfo.CurrentUICulture.Name;
        return client;
    }



    /// <summary>
    /// 确保角色带有可用的网页 Cookie。
    /// <para>
    /// 0.19 的 Cookie 由通行证在「更新游戏角色」时派生并写库；角色行存在但 Cookie 为空
    /// （例如只写过角色没走过通行证刷新）时，这里借 <see cref="GameRecordRoleService.UpdateGameRoleAsync"/>
    /// 触发一次刷新再重读。仍拿不到就按登录态失效上报，交给账号中心模块处理重新登录。
    /// </para>
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>带 Cookie 的角色；Cookie 仍为空时抛出登录态失效异常。</returns>
    private async Task<GameRecordRole> EnsureCookieAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(role.Cookie))
        {
            return role;
        }
        try
        {
            await _gameRecordRoleService.UpdateGameRoleAsync(role, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Refresh role cookie before sign-in failed (biz {biz}, uid {uid}).", role.GameBiz, role.Uid);
        }
        GameRecordRole? fresh = GameRecordRoleService.GetGameRoles(role.GameBiz).FirstOrDefault(x => x.Uid == role.Uid);
        if (fresh is not null && !string.IsNullOrWhiteSpace(fresh.Cookie))
        {
            role.Cookie = fresh.Cookie;
            return role;
        }
        throw new miHoYoApiException(SignInReturnCode.NotLoggedIn, "The role has no web cookie, please sign in again.");
    }



    /// <summary>
    /// 将米游社 API retcode 映射为 UI 可消费的结构化结果。
    /// </summary>
    /// <param name="returnCode">接口返回码。</param>
    /// <returns>对应的签到操作结果。</returns>
    private static SignInActionResult MapReturnCode(int returnCode)
    {
        return returnCode switch
        {
            SignInReturnCode.AlreadySignedIn => SignInActionResult.AlreadySigned,
            SignInReturnCode.NotEnoughCoin => SignInActionResult.NotEnoughCoin,
            SignInReturnCode.ResignQuotaUsedUp => SignInActionResult.ResignQuotaUsedUp,
            SignInReturnCode.NoAvailableResignDate => SignInActionResult.NoResignDate,
            SignInReturnCode.PleaseSignInFirst => SignInActionResult.PleaseSignInFirst,
            _ => SignInActionResult.Failed,
        };
    }



    /// <summary>
    /// 判断签到响应是否要求进行极验或其他风控验证。
    /// </summary>
    /// <param name="result">签到接口成功返回的数据。</param>
    /// <returns>存在任一风控标记时返回 true。</returns>
    private static bool IsRiskControl(SignInResult result)
    {
        return result.Success is 1
            || result.RiskCode != 0
            || result.IsRisk
            || !string.IsNullOrWhiteSpace(result.Gt)
            || !string.IsNullOrWhiteSpace(result.Challenge);
    }



    /// <summary>
    /// 获取本月奖励列表，优先读内存 / KVT 缓存，跨月或缺失 today 时回源 API。
    /// </summary>
    /// <param name="client">当前角色对应的 Client。</param>
    /// <param name="role">游戏角色。</param>
    /// <param name="info">签到状态，提供服务器日期用于缓存键与月份校验。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当月奖励列表。</returns>
    private async Task<SignInReward> GetSignInRewardCachedAsync(SignInClientBase client, GameRecordRole role, SignInRewardInfo info, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParse(info.Today, out DateOnly serverToday))
        {
            _logger.LogDebug("Sign-in info.Today is missing, skipping reward cache (biz {biz})", role.GameBiz);
            return await client.GetSignInRewardAsync(role, cancellationToken);
        }

        string rewardKey = GetRewardCacheKey(role.GameBiz, serverToday);
        if (TryGetCachedReward(rewardKey, serverToday.Month, out SignInReward? cached) && cached is not null)
        {
            return cached;
        }

        SignInReward reward = await client.GetSignInRewardAsync(role, cancellationToken);
        if (reward.Month == serverToday.Month)
        {
            SetCachedReward(rewardKey, reward);
        }

        return reward;
    }



    /// <summary>生成奖励列表缓存键（游戏 + 服务器年月）。</summary>
    private static string GetRewardCacheKey(string gameBiz, DateOnly serverToday)
        => $"sign_in_reward_{gameBiz}_{serverToday:yyyyMM}";



    /// <summary>
    /// 尝试从内存或 KVT 读取奖励列表，并校验月份一致。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="serverMonth">服务器当前月份，用于失效校验。</param>
    /// <param name="reward">命中时输出奖励列表。</param>
    /// <returns>是否命中有效缓存。</returns>
    private bool TryGetCachedReward(string key, int serverMonth, out SignInReward? reward)
    {
        if (_memoryCache.TryGetValue(key, out SignInReward? cached) && cached is not null && cached.Month == serverMonth)
        {
            reward = cached;
            return true;
        }

        if (DatabaseService.TryGetValue<string>(key, out string? json, out _) && !string.IsNullOrEmpty(json))
        {
            try
            {
                SignInReward? fromDb = JsonSerializer.Deserialize<SignInReward>(json, AppConfig.JsonSerializerOptions);
                if (fromDb is not null && fromDb.Month == serverMonth)
                {
                    _memoryCache.Set(key, fromDb);
                    reward = fromDb;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to deserialize sign-in reward cache (key {key})", key);
            }
        }

        reward = null;
        return false;
    }



    /// <summary>
    /// 将奖励列表写入内存与 KVT 持久化缓存。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="reward">奖励列表。</param>
    private void SetCachedReward(string key, SignInReward reward)
    {
        _memoryCache.Set(key, reward);
        string json = JsonSerializer.Serialize(reward, AppConfig.JsonSerializerOptions);
        DatabaseService.SetValue(key, json);
    }


}



/// <summary>
/// 签到卡片所需的整体状态
/// </summary>
internal class SignInStatus
{
    /// <summary>本月每日奖励列表。</summary>
    public SignInReward Reward { get; init; } = default!;

    /// <summary>当前签到状态（已签天数、今日是否已签）。</summary>
    public SignInRewardInfo Info { get; init; } = default!;

    /// <summary>补签信息，部分游戏可能为 null。</summary>
    public SignInResignInfo? ResignInfo { get; init; }
}



/// <summary>
/// 签到 / 补签操作的结构化结果
/// </summary>
internal enum SignInActionResult
{
    /// <summary>签到 / 补签成功。</summary>
    Success,
    /// <summary>今日已签到。</summary>
    AlreadySigned,
    /// <summary>Cookie 失效或未登录。</summary>
    CookieExpired,
    /// <summary>触发风控，需极验验证。</summary>
    RiskControl,
    /// <summary>补签货币不足。</summary>
    NotEnoughCoin,
    /// <summary>补签次数已用尽。</summary>
    ResignQuotaUsedUp,
    /// <summary>没有可补签的日期。</summary>
    NoResignDate,
    /// <summary>需先完成今日签到才能补签。</summary>
    PleaseSignInFirst,
    /// <summary>其他未知失败。</summary>
    Failed,
}



/// <summary>
/// 表示签到操作结果及其保留的服务端诊断信息。
/// </summary>
/// <param name="Kind">可供业务逻辑判断的结果类型。</param>
/// <param name="ReturnCode">接口 retcode；没有 retcode 的响应为 null。</param>
/// <param name="ResponseMessage">接口原始消息；未知失败时由 UI 显示。</param>
internal sealed record SignInActionResponse(SignInActionResult Kind, int? ReturnCode, string? ResponseMessage);
