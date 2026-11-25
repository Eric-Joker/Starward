using Microsoft.Extensions.Logging;
using Starward.Core.Passport;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.Account;

/// <summary>
/// 短信验证码登录里正在进行的操作。极验验证码的结果只会用在同一操作上，
/// 发码时拿到的 action_type 也只属于发码这条线。
/// </summary>
internal enum PassportSmsOperation
{
    /// <summary>没有进行中的操作。</summary>
    None = 0,

    /// <summary>发送登录短信验证码。</summary>
    SendSmsCode = 1,

    /// <summary>使用短信验证码登录。</summary>
    Login = 2,
}


/// <summary>
/// 短信验证码登录的会话状态。
/// <para>
/// 账号中心有两个宿主：主界面里常驻的面板、工具箱入口每次新建的面板。登录过程中面板可能被重建或收起
/// （窗口进托盘、切页面等），所以“发到哪个手机、服务器给的 action_type、发送时间与倒计时、
/// 触发风控的 aigis 任务、已经解出但还没随请求发出去的极验结果、正在进行的请求”都不能放在面板里，
/// 否则重开面板后用户只能重新过一次验证码。
/// </para>
/// <para>
/// 生命周期：用户开始发码/登录时创建并沿用，登录成功或用户显式取消时 <see cref="Reset"/>，
/// 或者超过 <see cref="SessionLifetime"/> 没有活动。宿主关闭（非取消）不清状态。
/// </para>
/// </summary>
internal class PassportLoginSession
{


    /// <summary>
    /// 已经解出、但还没随请求发出去的极验结果最多保留这么久。超过后服务端会判定验证码失效，只能重新过。
    /// </summary>
    public static TimeSpan CaptchaLifetime { get; } = TimeSpan.FromSeconds(110);


    /// <summary>一次登录会话（从发码到登录完成）最长保留这么久，避免很久以前的 action_type 被误用。</summary>
    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromMinutes(10);


    /// <summary>验证码最多连续过几次，防止服务端一直要求人机验证时死循环。</summary>
    private const int MaxCaptchaAttempts = 3;


    private readonly ILogger<PassportLoginSession> _logger;


    private readonly object _lock = new();


    /// <summary>会话代数。Reset 之后旧流程的续体不能再写状态，避免取消后又被写回。</summary>
    private int _epoch;


    private CancellationTokenSource _cancellation = new();


    private string? _mobile;

    private string? _smsActionType;

    private DateTimeOffset _smsSentTime;

    private int _smsCountdown = 60;


    private PassportAigisHeader? _pendingAigis;

    private PassportSmsOperation _pendingAigisOperation;


    private PassportCaptcha? _solvedCaptcha;

    private PassportSmsOperation _solvedCaptchaOperation;

    private string? _solvedCaptchaMobile;

    private DateTimeOffset _captchaSolvedTime;


    private DateTimeOffset _lastActivityTime;


    private Task<PassportLoginOutcome>? _pendingSendTask;

    private Task<PassportLoginOutcome>? _pendingLoginTask;


    public PassportLoginSession(ILogger<PassportLoginSession> logger)
    {
        _logger = logger;
        _lastActivityTime = DateTimeOffset.Now;
    }




    /// <summary>当前会话的取消令牌。用户显式取消后，所有在途请求都会跟着取消。</summary>
    public CancellationToken CancellationToken
    {
        get
        {
            lock (_lock)
            {
                return _cancellation.Token;
            }
        }
    }


    /// <summary>最近一次发码使用的手机号。</summary>
    public string? Mobile
    {
        get
        {
            lock (_lock)
            {
                return IsExpired(DateTimeOffset.Now) ? null : _mobile;
            }
        }
    }


    /// <summary>最近一次发码成功返回的 action_type，登录时原样带回。</summary>
    public string? SmsActionType
    {
        get
        {
            lock (_lock)
            {
                return IsExpired(DateTimeOffset.Now) ? null : _smsActionType;
            }
        }
    }


    /// <summary>是否已经成功发出过短信验证码（登录按钮需要 action_type）。</summary>
    public bool HasSentSmsCode => !string.IsNullOrWhiteSpace(SmsActionType);


    /// <summary>最近一次触发风控时的 aigis 任务。</summary>
    public PassportAigisHeader? PendingAigis
    {
        get
        {
            lock (_lock)
            {
                return IsExpired(DateTimeOffset.Now) ? null : _pendingAigis;
            }
        }
    }


    /// <summary>最近一次进入验证码态的操作（发码或登录）。</summary>
    public PassportSmsOperation PendingOperation
    {
        get
        {
            lock (_lock)
            {
                return IsExpired(DateTimeOffset.Now) ? PassportSmsOperation.None : _pendingAigisOperation;
            }
        }
    }




    /// <summary>
    /// 开始一次发码 / 登录。手机号没变且在会话有效期内就沿用之前的 action_type、倒计时和验证码；
    /// 换了手机号或者会话已过期则从空白开始。
    /// </summary>
    public void Begin(string mobile)
    {
        lock (_lock)
        {
            bool restart = IsExpired(DateTimeOffset.Now) || !string.Equals(_mobile, mobile, StringComparison.Ordinal);
            if (restart)
            {
                ResetCore();
                _mobile = mobile;
            }
            _lastActivityTime = DateTimeOffset.Now;
        }
    }




    /// <summary>
    /// 清空整个会话并取消在途请求。只在用户显式取消登录、或者登录成功之后调用；
    /// 宿主关闭（非取消）不清，否则重开面板又要重新过验证码。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _logger.LogInformation("Passport login session reset.");
            ResetCore();
        }
    }




    private void ResetCore()
    {
        _epoch++;
        try
        {
            _cancellation.Cancel();
        }
        catch { }
        // 旧令牌源不再使用，但不 Dispose：在途请求可能还持有它的 Token。
        _cancellation = new CancellationTokenSource();
        _mobile = null;
        _smsActionType = null;
        _smsSentTime = default;
        _smsCountdown = 60;
        _pendingAigis = null;
        _pendingAigisOperation = PassportSmsOperation.None;
        _solvedCaptcha = null;
        _solvedCaptchaOperation = PassportSmsOperation.None;
        _solvedCaptchaMobile = null;
        _captchaSolvedTime = default;
        _pendingSendTask = null;
        _pendingLoginTask = null;
        _lastActivityTime = DateTimeOffset.Now;
    }




    private bool IsExpired(DateTimeOffset now)
    {
        return now - _lastActivityTime > SessionLifetime;
    }




    /// <summary>距离可以重新发送验证码还有多少秒；0 表示现在就能发。</summary>
    public int GetRemainingCountdownSeconds()
    {
        lock (_lock)
        {
            if (_smsSentTime == default || _smsCountdown <= 0)
            {
                return 0;
            }
            double remaining = (_smsSentTime + TimeSpan.FromSeconds(_smsCountdown) - DateTimeOffset.Now).TotalSeconds;
            if (remaining <= 0)
            {
                return 0;
            }
            return (int)Math.Ceiling(Math.Min(remaining, 600));
        }
    }




    private bool IsOutdated(int epoch)
    {
        lock (_lock)
        {
            return epoch != _epoch;
        }
    }




    /// <summary>记录触发风控的 aigis 任务，重发时要用同一个会话。</summary>
    private void StoreAigis(PassportAigisHeader? aigis, PassportSmsOperation operation, int epoch)
    {
        if (aigis is null)
        {
            return;
        }
        lock (_lock)
        {
            if (epoch != _epoch)
            {
                return;
            }
            _pendingAigis = aigis;
            _pendingAigisOperation = operation;
            _lastActivityTime = DateTimeOffset.Now;
        }
    }




    /// <summary>记录已经解出的极验结果，供“同一操作的下一次请求”直接复用。</summary>
    private void StoreCaptcha(string mobile, PassportSmsOperation operation, PassportCaptcha captcha, int epoch)
    {
        lock (_lock)
        {
            if (epoch != _epoch)
            {
                return;
            }
            _solvedCaptcha = captcha;
            _solvedCaptchaOperation = operation;
            _solvedCaptchaMobile = mobile;
            _captchaSolvedTime = DateTimeOffset.Now;
            _lastActivityTime = DateTimeOffset.Now;
        }
    }




    /// <summary>
    /// 取出可复用的极验结果。只有同手机号、同操作、在 <see cref="CaptchaLifetime"/> 内的结果才会返回；
    /// 取出即视为已经随请求发出，不能再次复用（所以这里会把它清掉）。
    /// </summary>
    private PassportCaptcha? TakeCaptcha(string mobile, PassportSmsOperation operation, int epoch)
    {
        lock (_lock)
        {
            if (epoch != _epoch || _solvedCaptcha is null)
            {
                return null;
            }
            if (_solvedCaptchaOperation != operation || !string.Equals(_solvedCaptchaMobile, mobile, StringComparison.Ordinal))
            {
                return null;
            }
            PassportCaptcha captcha = _solvedCaptcha;
            bool expired = DateTimeOffset.Now - _captchaSolvedTime > CaptchaLifetime;
            _solvedCaptcha = null;
            _solvedCaptchaOperation = PassportSmsOperation.None;
            _solvedCaptchaMobile = null;
            _captchaSolvedTime = default;
            if (expired)
            {
                _logger.LogInformation("Solved captcha expired before it could be sent.");
                return null;
            }
            _lastActivityTime = DateTimeOffset.Now;
            return captcha;
        }
    }




    /// <summary>记录发码成功的结果：action_type 与重新发送的倒计时。</summary>
    private void StoreSmsResult(string mobile, string actionType, int countdown, int epoch)
    {
        lock (_lock)
        {
            if (epoch != _epoch)
            {
                return;
            }
            _mobile = mobile;
            _smsActionType = actionType;
            _smsSentTime = DateTimeOffset.Now;
            _smsCountdown = countdown > 0 ? countdown : 60;
            _lastActivityTime = DateTimeOffset.Now;
        }
    }




    /// <summary>
    /// 发送登录短信验证码。遇到风控验证码时自动重发：验证码结果一到，就在同一个操作上带着
    /// <c>x-rpc-aigis</c> 立刻重发，不需要用户再点一次“发送验证码”。
    /// <para>
    /// 同一时间只会有一个发送流程：重复点击只会等待正在进行的那个，不会重复发短信、也不会再弹一次验证码。
    /// </para>
    /// </summary>
    /// <param name="mobile">手机号。</param>
    /// <param name="sendRequest">真正发出请求的函数，参数是本次要带上的极验结果（可能为 null）。</param>
    /// <param name="resolveCaptcha">弹出人机验证并等待结果，取消时返回 null。</param>
    public Task<PassportLoginOutcome> SendSmsCodeAsync(
        string mobile,
        Func<PassportCaptcha?, Task<PassportLoginOutcome>> sendRequest,
        Func<PassportAigisHeader, Task<PassportCaptcha?>> resolveCaptcha)
    {
        lock (_lock)
        {
            if (_pendingSendTask is { IsCompleted: false })
            {
                _logger.LogInformation("Reuse the pending sms captcha request.");
                return _pendingSendTask;
            }
            int epoch = _epoch;
            _pendingSendTask = SendSmsCodeCoreAsync(epoch, mobile, sendRequest, resolveCaptcha);
            return _pendingSendTask;
        }
    }




    private async Task<PassportLoginOutcome> SendSmsCodeCoreAsync(
        int epoch,
        string mobile,
        Func<PassportCaptcha?, Task<PassportLoginOutcome>> sendRequest,
        Func<PassportAigisHeader, Task<PassportCaptcha?>> resolveCaptcha)
    {
        try
        {
            for (int attempt = 0; attempt < MaxCaptchaAttempts; attempt++)
            {
                if (IsOutdated(epoch))
                {
                    return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
                }
                // 已经解出、还没发出去的验证码直接复用，不重复弹验证码。
                PassportCaptcha? captcha = TakeCaptcha(mobile, PassportSmsOperation.SendSmsCode, epoch);
                var outcome = await sendRequest(captcha);
                if (IsOutdated(epoch))
                {
                    return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
                }
                if (outcome.State is PassportLoginState.NeedCaptcha)
                {
                    if (outcome.Aigis is null)
                    {
                        _logger.LogWarning("Create login sms captcha requires a captcha but the aigis header is missing.");
                        return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaUnavailable);
                    }
                    StoreAigis(outcome.Aigis, PassportSmsOperation.SendSmsCode, epoch);
                    PassportCaptcha? resolved = await resolveCaptcha(outcome.Aigis);
                    if (resolved is null || IsOutdated(epoch))
                    {
                        return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
                    }
                    StoreCaptcha(mobile, PassportSmsOperation.SendSmsCode, resolved, epoch);
                    // 结果一到就继续循环：下一轮会取出刚存下的验证码并立即带上 x-rpc-aigis 重发。
                    continue;
                }
                if (outcome.IsSuccess)
                {
                    // 没有 action_type 的“成功”是假成功：后面登录一定失败，这里直接按失败处理。
                    if (string.IsNullOrWhiteSpace(outcome.ActionType))
                    {
                        _logger.LogWarning("Create login sms captcha succeeded but no action_type was returned.");
                        return PassportLoginOutcome.Failed(Lang.PassportLogin_ActionTypeMissing);
                    }
                    StoreSmsResult(mobile, outcome.ActionType, outcome.Countdown, epoch);
                    return outcome;
                }
                return outcome;
            }
            return PassportLoginOutcome.Failed(Lang.PassportLogin_TooManyAttempts);
        }
        catch (OperationCanceledException)
        {
            return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send login sms captcha");
            return PassportLoginOutcome.Failed(ex);
        }
    }




    /// <summary>
    /// 使用短信验证码登录。同样会复用会话里已经解出的极验结果，需要时自动带 <c>x-rpc-aigis</c> 重试，
    /// 而不是把验证码结果丢掉让用户再点一次。
    /// </summary>
    /// <param name="mobile">手机号。</param>
    /// <param name="sendRequest">真正发出登录请求的函数，参数是本次要带上的极验结果（可能为 null）。</param>
    /// <param name="resolveCaptcha">弹出人机验证并等待结果，取消时返回 null。</param>
    public Task<PassportLoginOutcome> LoginAsync(
        string mobile,
        Func<PassportCaptcha?, Task<PassportLoginOutcome>> sendRequest,
        Func<PassportAigisHeader, Task<PassportCaptcha?>> resolveCaptcha)
    {
        lock (_lock)
        {
            if (_pendingLoginTask is { IsCompleted: false })
            {
                _logger.LogInformation("Reuse the pending sms login request.");
                return _pendingLoginTask;
            }
            int epoch = _epoch;
            _pendingLoginTask = LoginCoreAsync(epoch, mobile, sendRequest, resolveCaptcha);
            return _pendingLoginTask;
        }
    }




    private async Task<PassportLoginOutcome> LoginCoreAsync(
        int epoch,
        string mobile,
        Func<PassportCaptcha?, Task<PassportLoginOutcome>> sendRequest,
        Func<PassportAigisHeader, Task<PassportCaptcha?>> resolveCaptcha)
    {
        try
        {
            for (int attempt = 0; attempt < MaxCaptchaAttempts; attempt++)
            {
                if (IsOutdated(epoch))
                {
                    return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
                }
                PassportCaptcha? captcha = TakeCaptcha(mobile, PassportSmsOperation.Login, epoch);
                var outcome = await sendRequest(captcha);
                if (IsOutdated(epoch))
                {
                    return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
                }
                if (outcome.State is PassportLoginState.NeedCaptcha)
                {
                    if (outcome.Aigis is null)
                    {
                        _logger.LogWarning("Sms login requires a captcha but the aigis header is missing.");
                        return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaUnavailable);
                    }
                    StoreAigis(outcome.Aigis, PassportSmsOperation.Login, epoch);
                    PassportCaptcha? resolved = await resolveCaptcha(outcome.Aigis);
                    if (resolved is null || IsOutdated(epoch))
                    {
                        return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
                    }
                    StoreCaptcha(mobile, PassportSmsOperation.Login, resolved, epoch);
                    continue;
                }
                if (outcome.IsSuccess)
                {
                    // 登录成功，整个会话的临时凭据都不需要了。
                    Reset();
                }
                return outcome;
            }
            return PassportLoginOutcome.Failed(Lang.PassportLogin_TooManyAttempts);
        }
        catch (OperationCanceledException)
        {
            return PassportLoginOutcome.Failed(Lang.PassportLogin_CaptchaCancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login by sms captcha");
            return PassportLoginOutcome.Failed(ex);
        }
    }


}