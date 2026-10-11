using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Core.Passport;
using Starward.Features.Account;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameLauncher;

/// <summary>
/// 游戏授权登录（自动登录）。
/// <para>
/// 使用通行证令牌向服务器申请游戏授权票据，游戏启动后会自动登录到对应的账号。
/// 令牌由 <see cref="PassportAccountService"/> 统一维护，与米游社工具箱共用。
/// </para>
/// </summary>
internal class GameAuthLoginService
{


    private readonly ILogger<GameAuthLoginService> _logger;

    private readonly PassportClient _client;

    private readonly PassportAccountService _accountService;


    public GameAuthLoginService(ILogger<GameAuthLoginService> logger, PassportClient client, PassportAccountService accountService)
    {
        _logger = logger;
        _client = client;
        _accountService = accountService;
    }




    /// <summary>是否已经登录通行证账号。</summary>
    public bool IsLoggedIn => _accountService.IsLoggedIn;


    /// <summary>当前登录的通行证 ID。</summary>
    public long? HyperionAid => _accountService.Aid > 0 ? _accountService.Aid : null;




    /// <summary>
    /// 获取通行证 ID。令牌需要刷新时才会发出请求。
    /// </summary>
    public async Task<long?> GetHyperionAidAsync(CancellationToken cancellationToken = default)
    {
        if (!_accountService.IsLoggedIn)
        {
            return null;
        }
        await _accountService.RefreshIfNeededAsync(false, cancellationToken);
        return HyperionAid;
    }




    /// <summary>
    /// 申请指定游戏的授权票据，失败时返回 null。
    /// </summary>
    public async Task<string?> CreateAuthTicketByGameBiz(GameId gameId)
    {
        try
        {
            if (gameId.GameBiz.Server is not "cn")
            {
                return null;
            }
            var tokenSet = _accountService.TokenSet;
            if (tokenSet?.HasStoken is not true || string.IsNullOrWhiteSpace(tokenSet.Mid))
            {
                _logger.LogInformation("Skip auth ticket for {biz}: no passport account signed in.", gameId?.GameBiz);
                return null;
            }
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var cancellationToken = cancellationTokenSource.Token;
            // 每个游戏各记一个通行证账号，启动前先让会话对到这个游戏记录过的账号。
            await _accountService.UseAccountForGameAsync(gameId.GameBiz, cancellationToken);
            tokenSet = _accountService.TokenSet;
            if (tokenSet?.HasStoken is not true || string.IsNullOrWhiteSpace(tokenSet.Mid))
            {
                _logger.LogInformation("Skip auth ticket for {biz}: the recorded passport account is not available.", gameId?.GameBiz);
                return null;
            }
            if (!await _accountService.RefreshIfNeededAsync(false, cancellationToken))
            {
                _logger.LogWarning("Skip auth ticket for {biz}: passport token can not be refreshed.", gameId?.GameBiz);
                return null;
            }
            // 设备指纹只在本机数据库中读取，不会产生额外请求。
            if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceId))
            {
                _client.DeviceId = AppConfig.HyperionDeviceId;
            }
            if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceFp))
            {
                _client.DeviceFp = AppConfig.HyperionDeviceFp;
            }
            var result = await _client.CreateAuthTicketByGameBizAsync(gameId.GameBiz.ToString(), tokenSet.Stoken!, tokenSet.Mid!, tokenSet.Aid, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Create auth ticket failed: {retcode} {message}", result.Retcode, result.Message);
                return null;
            }
            string? ticket = result.Deserialize<PassportAuthTicket>()?.Value;
            if (string.IsNullOrWhiteSpace(ticket))
            {
                _logger.LogWarning("Create auth ticket returned an empty ticket.");
                return null;
            }
            _logger.LogInformation("Auth ticket created for {biz} (aid: {aid}).", gameId.GameBiz, tokenSet.Aid);
            return ticket;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create auth ticket by game biz ({biz})", gameId?.GameBiz);
        }
        return null;
    }




}
