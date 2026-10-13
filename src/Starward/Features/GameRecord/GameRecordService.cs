using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
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
using Starward.Core.Passport;
using Starward.Features.Account;
using Starward.Features.Database;
using Starward.Features.ViewHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord;

internal class GameRecordService
{


    private readonly ILogger<GameRecordService> _logger;

    private readonly HyperionClient _hyperionClient;

    private readonly HoyolabClient _hoyolabClient;

    private GameRecordClient _gameRecordClient;

    private readonly IMemoryCache _memoryCache;


    /// <summary>
    /// 国服接口失败后重刷设备指纹的最小间隔。避免一次风控失败就让每个请求都去申请新指纹、反而更容易被拦。
    /// <para>
    /// 时间戳只存在进程里，重启后会重新计时，也就是说重启后可能比这里写的间隔更早刷新一次。
    /// 持久化需要给 AppConfig 加字段，超出了本次改动的范围。
    /// </para>
    /// </summary>
    private static readonly TimeSpan DeviceFpFailureUpdateCooldown = TimeSpan.FromHours(6);


    /// <summary>上次因请求失败而尝试刷新设备指纹的时间（进程内，见 <see cref="DeviceFpFailureUpdateCooldown"/>）。</summary>
    private static DateTimeOffset _lastDeviceFpFailureAttempt;


    /// <summary>串行化因请求失败触发的指纹刷新，避免并发请求同时向 public-data-api 申请指纹。</summary>
    private static readonly SemaphoreSlim _deviceFpFailureUpdateLock = new(1, 1);


    /// <summary>人机验证面板同一时刻只允许一个，验证期间请求可能来自任意后台线程。</summary>
    private static readonly SemaphoreSlim _captchaPanelLock = new(1, 1);


    /// <summary>
    /// 显式抑制人机验证的深度（可嵌套）。大于 0 时遇到 aigis 不弹验证码，直接把原异常抛给调用方。
    /// </summary>
    private static readonly AsyncLocal<int> _interactiveCaptchaSuppressDepth = new();


    /// <summary>
    /// 显式放行人机验证的深度（可嵌套）。只有用户自己点出来的那条路径才需要它。
    /// </summary>
    private static readonly AsyncLocal<int> _interactiveCaptchaAllowDepth = new();


    /// <summary>
    /// 后台自动更新整轮共用的请求间隔；为 null 时不做额外间隔，手动刷新保持原速。
    /// </summary>
    private static readonly AsyncLocal<Func<CancellationToken, Task>?> _requestPace = new();


    public string Language { get => _hoyolabClient.Language; set => _hoyolabClient.Language = value; }


    private bool isHoyolab;
    public bool IsHoyolab
    {
        get => isHoyolab;
        set
        {
            if (value)
            {
                _gameRecordClient = _hoyolabClient;
            }
            else
            {
                _gameRecordClient = _hyperionClient;
            }
            isHoyolab = value;
        }
    }


    public GameRecordService(ILogger<GameRecordService> logger, HyperionClient hyperionClient, HoyolabClient hoyolabClient, IMemoryCache memoryCache)
    {
        _logger = logger;
        _hyperionClient = hyperionClient;
        _hoyolabClient = hoyolabClient;
        _gameRecordClient = hyperionClient;
        _memoryCache = memoryCache;
    }


    /// <summary>
    /// 为当前异步流禁止弹人机验证：遇到 aigis 时直接抛出原异常。
    /// 可嵌套，作用域结束（Dispose）后恢复。
    /// </summary>
    /// <returns>结束抑制的作用域。</returns>
    public static IDisposable SuppressInteractiveCaptcha()
    {
        _interactiveCaptchaSuppressDepth.Value++;
        return new InteractiveCaptchaScope(_interactiveCaptchaSuppressDepth);
    }


    /// <summary>
    /// 为当前异步流放行人机验证：只有明确由用户点击触发的路径才应该调用。
    /// 弹验证码面板会加载极验（<c>static.geetest.com</c>）的第三方脚本，属于向非 mihoyo 服务发起请求，
    /// 所以放行必须显式声明。抑制与放行同时存在时以抑制为准。
    /// </summary>
    /// <returns>结束放行的作用域。</returns>
    public static IDisposable AllowInteractiveCaptcha()
    {
        _interactiveCaptchaAllowDepth.Value++;
        return new InteractiveCaptchaScope(_interactiveCaptchaAllowDepth);
    }


    /// <summary>
    /// 当前异步流是否禁止弹出人机验证。
    /// <para>
    /// 【刻意选择失败安全方向】默认就是抑制：只有显式 <see cref="AllowInteractiveCaptcha"/> 过的
    /// 用户交互路径才允许加载极验；其余一切路径（后台自动更新、静默刷新、以及任何忘记声明的调用）
    /// 都跳过弹窗、直接把风控异常交给调用方。宁可少弹一次验证码（用户重新登录或在工具箱里粘贴
    /// Cookie 都能恢复），也不能在用户没有点的情况下自动向第三方发出请求。
    /// </para>
    /// </summary>
    public static bool IsInteractiveCaptchaSuppressed
        => _interactiveCaptchaSuppressDepth.Value > 0 || _interactiveCaptchaAllowDepth.Value <= 0;


    /// <summary>
    /// 结束 <see cref="SuppressInteractiveCaptcha"/> 或 <see cref="AllowInteractiveCaptcha"/> 的作用域。
    /// </summary>
    private sealed class InteractiveCaptchaScope : IDisposable
    {

        private AsyncLocal<int>? _depth;

        public InteractiveCaptchaScope(AsyncLocal<int> depth)
        {
            _depth = depth;
        }

        public void Dispose()
        {
            if (_depth is null)
            {
                return;
            }
            int value = _depth.Value;
            _depth.Value = value > 0 ? value - 1 : 0;
            _depth = null;
        }

    }


    /// <summary>
    /// 为当前异步流套上请求间隔。后台自动更新的整轮请求（含月报翻页等内部请求）都按这个节奏发，
    /// 手动刷新没有设置间隔，保持原速。
    /// </summary>
    /// <param name="pace">间隔委托，通常与自动更新外层的 3–8 秒节奏是同一个。</param>
    /// <returns>结束间隔的作用域。</returns>
    internal static IDisposable UseRequestPacing(Func<CancellationToken, Task> pace)
    {
        ArgumentNullException.ThrowIfNull(pace);
        Func<CancellationToken, Task>? previous = _requestPace.Value;
        _requestPace.Value = pace;
        return new RequestPacingScope(previous);
    }


    /// <summary>
    /// 按当前异步流的间隔等待一次；没有设置间隔（手动刷新）时立即返回。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task PaceRequestAsync(CancellationToken cancellationToken)
    {
        if (_requestPace.Value is { } pace)
        {
            await pace(cancellationToken);
        }
    }


    /// <summary>
    /// 结束 <see cref="UseRequestPacing"/> 的作用域，恢复外层（若有）的间隔。
    /// </summary>
    private sealed class RequestPacingScope : IDisposable
    {

        private readonly Func<CancellationToken, Task>? _previous;

        private bool _disposed;

        public RequestPacingScope(Func<CancellationToken, Task>? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _requestPace.Value = _previous;
            _disposed = true;
        }

    }





    /// <summary>
    /// 业务错误是否带了需要用户交互才能完成的 aigis 人机验证任务。
    /// </summary>
    private static bool HasAigisChallenge(miHoYoApiException ex)
    {
        return ex.Aigis?.Data.ValueKind is JsonValueKind.Object or JsonValueKind.String;
    }




    /// <summary>
    /// 执行一次游戏战绩请求；遇到米哈游业务错误时按 TZ 的策略恢复一次。
    /// <para>
    /// 国服（<c>IsHoyolab</c> 为 false）遇到带 <c>x-rpc-aigis</c> 的风控错误时弹人机验证，带验证结果重试一次；
    /// 国际服（HoYoLAB）不走这条路径，直接抛出原始异常。
    /// 国服其余业务错误（登录态失效除外）先尝试刷新设备指纹，指纹确实换了才重试一次。
    /// 每次请求最多重试一次，恢复动作自身失败不会覆盖用户真正遇到的业务错误。
    /// </para>
    /// <para>
    /// 人机验证只在用户自己点出来的路径上弹（<see cref="AllowInteractiveCaptcha"/>），
    /// 默认抑制（<see cref="IsInteractiveCaptchaSuppressed"/>）：后台任务解不开验证码，弹窗既打扰用户、
    /// 又会在他没点的情况下加载极验的第三方脚本。
    /// </para>
    /// </summary>
    /// <typeparam name="T">请求返回类型。</typeparam>
    /// <param name="action">使用当前平台 Client 发起请求的委托。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>首次请求或一次恢复重试后的结果。</returns>
    private async Task<T> ExecuteWithGameRecordRecoveryAsync<T>(Func<GameRecordClient, Task<T>> action, CancellationToken cancellationToken = default)
    {
        GameRecordClient client = _gameRecordClient;
        // 用真正要发请求的那个 Client 判断平台，不依赖可能被并发请求改写的 IsHoyolab。
        bool isHoyolab = client is not HyperionClient;
        // 后台自动更新整轮在此统一排队，月报翻页等内部请求也不会连打；手动刷新没有任何间隔。
        await PaceRequestAsync(cancellationToken);
        try
        {
            return await action(client);
        }
        catch (miHoYoApiException ex)
        {
            if (!isHoyolab && HasAigisChallenge(ex))
            {
                if (IsInteractiveCaptchaSuppressed)
                {
                    // 后台 / 自动 / 未声明为用户交互的路径：不加载极验，直接把风控错误交回调用方，
                    // 由调用方记一条异常或提示用户重新登录。
                    _logger.LogWarning("Game record request requires an interactive captcha but it is suppressed (retcode {retcode}); the original exception is rethrown.", ex.ReturnCode);
                    throw;
                }
                var (handled, result) = await TryRetryAfterAigisAsync(client, ex, action, cancellationToken);
                if (handled)
                {
                    return result;
                }
            }
            if (isHoyolab)
            {
                // 国际服不申请设备指纹，没有人机验证以外的恢复手段。
                throw;
            }
            if (ex.IsLoginExpired)
            {
                // 登录态失效要换 Cookie 才有用，这里只保留原有的失败行为，不做无效重试。
                throw;
            }
            // 【加固，勿改】请求失败**绝不**清指纹、不重申请、不换身份：实测服务端只认请求头
            // x-rpc-device_id，重申请只会产出一个陌生身份，把可用状态永久换成 10041。
            _logger.LogWarning(
                "Game record request failed with retcode {retcode}; the device identity is left untouched.",
                ex.ReturnCode);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动取消（例如取消人机验证）时保持取消语义，不要当成接口失败。
            throw;
        }
    }




    /// <summary>
    /// 若业务错误带有 <c>x-rpc-aigis</c>，弹出人机验证并带挑战头重试一次。
    /// 当前异步流处于抑制状态时直接返回未处理，绝不创建验证码面板（见 <see cref="IsInteractiveCaptchaSuppressed"/>）。
    /// </summary>
    /// <param name="client">发起该请求的 Client，验证结果会临时写进去。</param>
    /// <param name="exception">带 aigis 载荷的原始业务错误。</param>
    /// <param name="action">重试时使用的请求委托。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成重试时为 (true, 结果)，否则为 (false, default)。</returns>
    private async Task<(bool Handled, T Result)> TryRetryAfterAigisAsync<T>(GameRecordClient client, miHoYoApiException exception, Func<GameRecordClient, Task<T>> action, CancellationToken cancellationToken)
    {
        if (IsInteractiveCaptchaSuppressed)
        {
            // 双保险：即使有人绕过上面的分支直接调到这里，也不会加载极验。
            return (false, default!);
        }
        string? aigisHeader = await ResolveGameRecordAigisAsync(exception.Aigis!, cancellationToken);
        if (string.IsNullOrWhiteSpace(aigisHeader))
        {
            return (false, default!);
        }
        ApplyGameRecordRiskHeaders(client, aigisHeader);
        try
        {
            var result = await action(client);
            return (true, result);
        }
        catch (Exception retryException)
        {
            throw;
        }
        finally
        {
            // 只在这一次重试期间生效：验证结果不能泄漏到后续请求上。
            client.RiskAigisHeader = null;
            client.RiskChallenge = null;
        }
    }




    /// <summary>
    /// 把极验结果写到 Client，供下一次请求带上 <c>x-rpc-aigis</c> / <c>x-rpc-challenge</c>。
    /// </summary>
    private static void ApplyGameRecordRiskHeaders(GameRecordClient client, string aigisHeader)
    {
        client.RiskAigisHeader = aigisHeader;
        client.RiskChallenge = TryReadGeetestChallenge(aigisHeader);
    }




    /// <summary>
    /// 从 <c>session_id;base64(验证结果)</c> 里取出 <c>geetest_challenge</c>，供 <c>x-rpc-challenge</c> 使用。
    /// 解析不出来时只带 aigis 头重试。
    /// </summary>
    private static string? TryReadGeetestChallenge(string aigisHeader)
    {
        try
        {
            int separator = aigisHeader.IndexOf(';');
            if (separator < 0 || separator >= aigisHeader.Length - 1)
            {
                return null;
            }
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(aigisHeader[(separator + 1)..]));
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("geetest_challenge", out JsonElement challenge) && challenge.ValueKind is JsonValueKind.String)
            {
                return challenge.GetString();
            }
            if (document.RootElement.TryGetProperty("challenge", out JsonElement challengeAlt) && challengeAlt.ValueKind is JsonValueKind.String)
            {
                return challengeAlt.GetString();
            }
        }
        catch
        {
            // 验证结果的 JSON 结构和预期不一致，交给接口判断。
        }
        return null;
    }




    /// <summary>
    /// 在主窗口上弹出人机验证面板，返回验证结果；无法显示时返回 null。
    /// <para>
    /// 面板（<see cref="PassportCaptchaPanel"/>）是普通控件，必须在 UI 线程上创建并挂到一个有 XamlRoot 的
    /// 弹层里，WebView2 才能初始化，所以这里显式 <see cref="Microsoft.UI.Dispatching.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueueHandler)"/>
    /// 过去，后台线程只异步等待结果，不阻塞 UI 线程。
    /// </para>
    /// </summary>
    /// <param name="aigis">接口返回的 aigis 载荷。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>aigis 请求头的值；用户取消或面板不可用时为 null。</returns>
    private async Task<string?> ResolveGameRecordAigisAsync(PassportAigisHeader aigis, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var challenge = aigis.GetChallenge();
        string sessionId = aigis.SessionId ?? "";
        if (challenge is null || string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("Cannot show game-record captcha: the aigis payload has no session id or geetest challenge.");
            return null;
        }
        MainWindow? window = MainWindow.Current;
        if (window?.Content?.XamlRoot is not { } xamlRoot)
        {
            _logger.LogWarning("Cannot show game-record captcha: MainWindow XamlRoot is unavailable.");
            return null;
        }
        await _captchaPanelLock.WaitAsync(cancellationToken);
        try
        {
            var tcs = new TaskCompletionSource<PassportCaptcha?>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 用 async void 的本地函数交给 DispatcherQueue：它在 UI 线程上跑，异常只回填 tcs，不会变成未观察的异常。
            async void ShowCaptchaOnUIThread()
            {
                try
                {
                    // 面板和承载它的弹层都必须由 UI 线程创建，DispatcherQueue 的队列本身就是 XamlRoot 所在的那棵树。
                    var panel = new PassportCaptchaPanel();
                    Popup popup = CreateCaptchaPopup(window, xamlRoot, panel);
                    try
                    {
                        popup.IsOpen = true;
                        tcs.TrySetResult(await panel.RunAsync(aigis, challenge));
                    }
                    finally
                    {
                        popup.IsOpen = false;
                    }
                }
                catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            if (!window.DispatcherQueue.TryEnqueue(ShowCaptchaOnUIThread))
            {
                _logger.LogWarning("Cannot show game-record captcha: the main window dispatcher rejected the work item.");
                return null;
            }
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            if (await tcs.Task is not { IsEmpty: false } captcha)
            {
                _logger.LogWarning("Game-record captcha was cancelled or did not return a result.");
                return null;
            }
            return captcha.ToAigisHeaderValue();
        }
        finally
        {
            _captchaPanelLock.Release();
        }
    }




    /// <summary>
    /// 创建一个承载人机验证面板的弹层，并把它放在主窗口中间。
    /// 只设置 <see cref="Popup.XamlRoot"/> 而不挂进主窗口的可视化树，避免把工具箱窗口和账号中心的布局搅在一起。
    /// </summary>
    private static Popup CreateCaptchaPopup(MainWindow window, XamlRoot xamlRoot, PassportCaptchaPanel panel)
    {
        var popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = panel,
            IsLightDismissEnabled = false,
        };
        try
        {
            double width = (window.Content as FrameworkElement)?.ActualWidth ?? 0;
            double height = (window.Content as FrameworkElement)?.ActualHeight ?? 0;
            if (width > 0 && height > 0)
            {
                popup.HorizontalOffset = Math.Max(0, (width - panel.WidgetWidth) / 2);
                popup.VerticalOffset = Math.Max(0, (height - panel.WidgetHeight) / 2);
            }
        }
        catch
        {
            // 取不到窗口尺寸就让弹层留在左上角，不影响验证本身。
        }
        return popup;
    }




    /// <summary>
    /// 国服接口返回业务错误后按冷却策略刷新一次设备指纹，供调用方决定是否值得重试。
    /// </summary>
    /// <param name="failedDeviceFp">触发失败请求所用的设备指纹，用于并发去重。</param>
    /// <param name="originalException">触发恢复流程的原始业务错误。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>指纹已刷新、或已被并发请求刷新时为 true；处于冷却期而跳过、或刷新失败时为 false。</returns>
    private async Task<bool> TryUpdateDeviceFpAfterRequestFailureAsync(string failedDeviceFp, miHoYoApiException originalException, CancellationToken cancellationToken)
    {
        // 【已停用，勿恢复】本方法原实现是"冷却期后清指纹并重新申请一枚"。
        // 实测服务端只认请求头 x-rpc-device_id，重申请得到的必然是一枚属于陌生身份的指纹，
        // 只会把可用状态换成永久 10041（曾把可用的 d5c54418-… 换掉，只能手工改库恢复）。
        // 调用点已从 ExecuteWithGameRecordRecoveryAsync 移除；这里再短路一次，保证即使有人重新接线
        // 也不可能触发清指纹 / 重申请 / 换身份。
        await Task.CompletedTask;
        _ = failedDeviceFp;
        _ = originalException;
        _ = cancellationToken;
        return false;
        /* 以下为停用前的原实现，保留仅作说明：
        try
        {
            await _deviceFpFailureUpdateLock.WaitAsync(cancellationToken);
            try
            {
                // 指纹已经被另一个并发失败请求换掉了，直接用新的重试即可。
                if (!string.Equals(_hyperionClient.DeviceFp, failedDeviceFp, StringComparison.Ordinal))
                {
                    return true;
                }
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset lastAttempt = _lastDeviceFpFailureAttempt;
                // 一般要等 6 小时冷却；但"带账号 Cookie 登记指纹"这条新路径（B 组）允许在冷却期内多试一次，
                // 判据见 CanReregisterDeviceFpWithAccountCookie（带 Cookie 登记过仍失败就不再放行）。
                if (now - lastAttempt < DeviceFpFailureUpdateCooldown && !CanReregisterDeviceFpWithAccountCookie())
                {
                    return false;
                }
                // 先记时间再发请求：即使申请失败也不会在冷却期内反复重试。
                _lastDeviceFpFailureAttempt = now;
                // 这一条恢复路径是换指纹的第二个（也是最后一个）入口，同样受冷却期约束：
                // 先把旧指纹挪到一边，再让 UpdateHyperionDeviceFpAsync 因为「没有指纹」而申请新的。
                // 申请失败就把旧指纹放回去——它只是被这个接口拒了，没有理由丢掉。
                string? previousFp = AppConfig.HyperionDeviceFp;
                DateTimeOffset previousUpdateTime = AppConfig.HyperionDeviceFpLastUpdateTime;
                ClearDeviceFp();
                try
                {
                    await UpdateHyperionDeviceFpAsync(false, cancellationToken);
                }
                catch
                {
                    RestoreDeviceFp(previousFp, previousUpdateTime);
                    throw;
                }
                return true;
            }
            finally
            {
                _deviceFpFailureUpdateLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 恢复动作失败不能覆盖用户真正遇到的业务错误，交给调用方继续抛原来的异常。
            return false;
        }
        */
    }




    /// <summary>
    /// 准备设备指纹。指纹是「设备身份令牌」，必须长期稳定：
    /// 只有完全没有指纹时才申请一枚；已经有指纹就原样使用，不按时间轮换、也不因为载荷版本变化而重申请。
    /// <para>
    /// 原因：服务端校验的是这枚指纹有没有被该账号登记过。实测把旧指纹写回、机型身份保持中性默认，
    /// 绝区零战绩立刻恢复正常；也就是说换指纹会直接破坏账号与设备的绑定关系，代价远大于刷新的收益。
    /// 便笺、绳网月报这些接口要的正是账号登记过的那一枚，用户粘贴进来的浏览器指纹见 <see cref="SaveDeviceFp"/>。
    /// </para>
    /// </summary>
    /// <param name="forceMint">
    /// 为 true 时即使已有指纹也重新申请一枚。**只给用户主动发起的入口用**（账号中心的「更新设备指纹」按钮），
    /// 自动流程一律传 false，否则会在用户不知情时换掉已登记的指纹。
    /// </param>
    public async Task UpdateDeviceFpAsync(bool forceMint = false, CancellationToken cancellationToken = default)
    {
        if (IsHoyolab)
        {
            return;
        }
        await UpdateHyperionDeviceFpAsync(forceMint, cancellationToken);
    }




    /// <summary>
    /// 用国服 Hyperion Client 载入设备指纹，只在「一枚都没有」时申请新的，并把最新值写回配置。
    /// 调用方已经确认这是国服场景时用它，不看共享的 <see cref="IsHoyolab"/>。
    /// <para>
    /// 重申请一律走「先清空再申请」：<see cref="ClearDeviceFp"/> 之后这里会因为「没有指纹」而申请一枚，
    /// 这样请求失败后的恢复路径与 <paramref name="forceMint"/> 共用同一条判据，不会有两套换指纹的条件。
    /// </para>
    /// </summary>
    /// <param name="forceMint">为 true 时即使已有指纹也重新申请一枚；只给用户主动发起的入口用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task UpdateHyperionDeviceFpAsync(bool forceMint, CancellationToken cancellationToken)
    {
        // 设备信息先写进去：国际服虽然不申请指纹，但鉴权信息在两边是共用的。
        LoadDeviceInfo();
        string? fp = AppConfig.HyperionDeviceFp;
        if (!string.IsNullOrWhiteSpace(fp) && !forceMint)
        {
            // 已经有指纹：直接用，不轮换。载荷版本变化只提示，绝不因此换掉账号登记过的这一枚。
            if (AppConfig.HyperionDeviceFpPayloadVersion < HyperionClient.DeviceFpPayloadVersion)
            {
                _logger.LogWarning(
                    "Stored device fingerprint was minted with ext_fields payload version {stored} (current {current}); keeping it because the server binds fingerprints to the account.",
                    AppConfig.HyperionDeviceFpPayloadVersion, HyperionClient.DeviceFpPayloadVersion);
            }
            return;
        }
        // 匿名申请。撤掉了"带账号 Cookie 申请"这条实验路径：实测米哈游的登录态接口、getFp、bbs、H5 页面
        // 都不下发 DEVICEFP 系列 Cookie，拿新 device_id 去调这些接口也登记不上（之后仍 10041）。
        await _hyperionClient.GetDeviceFpAsync(cancellationToken);
        // 【加固，勿改】这里**绝对不写** HyperionDeviceId / HyperionDeviceAndroidId。
        // 实测（9 组单变量）：服务端只认请求头 x-rpc-device_id —— 等于本机已登记的那个值就 retcode 0，
        // 换成任何陌生值就 10041（指纹用老的/浏览器的/新的、甚至完全不发 x-rpc-device_fp 都随它）。
        // 曾经因为在申请指纹成功后把这两个键连同指纹一起写回，把用户已登记的设备标识换成了全新值，
        // 导致绝区零战绩**永久** 10041，只能手工改库恢复。
        // 这两个键的唯一所有者是 LoadDeviceInfo()：只在为空时生成一次，之后终身不变。
        AppConfig.HyperionDeviceFp = _hyperionClient.DeviceFp;
        AppConfig.HyperionDeviceFpSeedId = _hyperionClient.DeviceFpSeedId;
        AppConfig.HyperionDeviceFpSeedTime = _hyperionClient.DeviceFpSeedTime;
        AppConfig.HyperionDeviceFpLastUpdateTime = DateTimeOffset.Now;
        AppConfig.HyperionDeviceFpPayloadVersion = HyperionClient.DeviceFpPayloadVersion;
    }




    /// <summary>
    /// 【B 组】取一条当前工具箱账号的 Cookie，供 getFp 请求带上。
    /// <para>
    /// 优先用**已经带着设备指纹的**角色行（如果有）：那条 Cookie 属于真正登记过设备的账号，
    /// 用它申请出来的指纹才有的可绑；没有这样的行时退回任意一条有内容、且带账号 ID 的角色行 Cookie
    /// （登录后角色行一定存在）。角色行都没有时再看 <c>GameRecordUser</c> 行；都拿不到就返回 null。
    /// </para>
    /// <para>
    /// 只用现有 Dapper 查询与 <see cref="GetCookieAccountId"/> 读库，不新增设置项，也不写任何数据。
    /// </para>
    /// </summary>
    /// <returns>账号 Cookie；拿不到时返回 null（此时照旧匿名申请）。</returns>
    private string? GetAccountCookieForDeviceFp()
    {
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            var roles = dapper.Query<GameRecordRole>(
                "SELECT * FROM GameRecordRole WHERE Cookie IS NOT NULL AND Cookie <> '';").ToList();
            GameRecordRole? withDeviceFp = roles.FirstOrDefault(static x => x.Cookie.Contains("DEVICEFP=", StringComparison.OrdinalIgnoreCase));
            if (withDeviceFp is not null)
            {
                return withDeviceFp.Cookie;
            }
            GameRecordRole? accountRole = roles.FirstOrDefault(static x => GetCookieAccountId(x.Cookie) > 0);
            if (accountRole is not null)
            {
                return accountRole.Cookie;
            }
            if (roles.Count > 0)
            {
                return roles[0].Cookie;
            }
            return dapper.QueryFirstOrDefault<string>("SELECT Cookie FROM GameRecordUser WHERE Cookie IS NOT NULL AND Cookie <> '' LIMIT 1;");
        }
        catch (Exception ex)
        {
            // 读不到账号 Cookie 只能退化成匿名申请，不能因此让指纹申请失败。
            _logger.LogWarning(ex, "Cannot read a toolbox account cookie for the getFp request; requesting anonymously.");
            return null;
        }
    }




    /// <summary>
    /// 【B 组】当前这枚指纹是否就是"带着账号 Cookie 申请出来的"那一枚。
    /// 只在本次进程内有效（<see cref="ClearDeviceFp"/> 会一并清零），因此最多多试一次，不会形成循环。
    /// </summary>
    private bool _deviceFpRegisteredWithAccountCookie;




    /// <summary>
    /// 【B 组】是否允许在 6 小时冷却期内为"带账号 Cookie 的申请"再多开一次机会。
    /// <para>
    /// 三条同时成立才放行（避免死循环）：
    /// 1. 当前指纹是"我们自申请的"（<see cref="CanPromptAccountDeviceFp"/>），不是用户粘贴的；
    /// 2. 当前这枚**不是**带账号 Cookie 申请出来的（<see cref="_deviceFpRegisteredWithAccountCookie"/> 为 false）
    ///    —— 也就是说还没试过新路径；一旦带 Cookie 登记过仍然 10041，这里就再也不放行；
    /// 3. 现在确实能拿到一条账号 Cookie（否则带 Cookie 申请无从谈起，直接走原来的 6 小时冷却）。
    /// </para>
    /// </summary>
    /// <returns>允许立即再登记一次时返回 true。</returns>
    private bool CanReregisterDeviceFpWithAccountCookie()
    {
        if (!CanPromptAccountDeviceFp() || _deviceFpRegisteredWithAccountCookie)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(GetAccountCookieForDeviceFp());
    }




    /// <summary>
    /// 清掉已存的设备指纹，让下一次 <see cref="UpdateDeviceFpAsync"/> 因为「没有指纹」而申请新的。
    /// <para>
    /// 只有「请求被服务端以风控拒绝、且冷却期已过」这一条恢复路径会用它（见
    /// <see cref="TryUpdateDeviceFpAfterRequestFailureAsync"/>）。用户自己贴进来的指纹由
    /// <see cref="SaveDeviceFp"/> 写入，不受影响。
    /// </para>
    /// <para>
    /// 同时把"这枚指纹是带账号 Cookie 登记出来的"标记清掉：指纹已经不存在了，下一次申请会重新判定，
    /// 这样"旧指纹是匿名申请的、新一次可以带 Cookie 登记"这条 B 组路径不会被旧标记挡住。
    /// </para>
    /// </summary>
    private void ClearDeviceFp()
    {
        AppConfig.HyperionDeviceFp = "";
        AppConfig.HyperionDeviceFpLastUpdateTime = default;
        _deviceFpRegisteredWithAccountCookie = false;
    }



    /// <summary>
    /// 把 <see cref="ClearDeviceFp"/> 清掉的指纹放回去（申请新指纹失败时用）。
    /// </summary>
    /// <param name="deviceFp">原来的指纹；空白时不写。</param>
    /// <param name="lastUpdateTime">原来的申请时间。</param>
    private static void RestoreDeviceFp(string? deviceFp, DateTimeOffset lastUpdateTime)
    {
        if (string.IsNullOrWhiteSpace(deviceFp))
        {
            return;
        }
        AppConfig.HyperionDeviceFp = deviceFp;
        AppConfig.HyperionDeviceFpLastUpdateTime = lastUpdateTime;
    }




    /// <summary>
    /// 保存用户指定的设备指纹，并立即对后续请求生效。
    /// <para>
    /// 服务端自己签发的指纹不一定被便笺、绳网月报这些接口接受，浏览器会话里正在用的那个反而能用，
    /// 所以留一个手动填入的入口。这里同时把更新时间重置为现在，让 3 天的轮换从这一刻重新起算，
    /// 免得刚填进去就被自动申请的新指纹覆盖掉。
    /// </para>
    /// </summary>
    /// <param name="deviceFp">设备指纹，空白表示不写入。</param>
    /// <returns>是否写入了一个有效的指纹。</returns>
    public bool SaveDeviceFp(string? deviceFp)
    {
        deviceFp = deviceFp?.Trim();
        if (string.IsNullOrWhiteSpace(deviceFp))
        {
            return false;
        }
        AppConfig.HyperionDeviceFp = deviceFp;
        AppConfig.HyperionDeviceFpLastUpdateTime = DateTimeOffset.Now;
        // 两个客户端都写上：国际服走 hoyolab 客户端，国服走 hyperion 客户端。
        _hyperionClient.DeviceFp = deviceFp;
        _hoyolabClient.DeviceFp = deviceFp;
        // 设备信息也一并从配置刷一遍，保证发出去的东西是同一套。
        LoadDeviceInfo();
        _logger.LogInformation("Device fingerprint set to {DeviceFp}.", deviceFp);
        return true;
    }




    /// <summary>
    /// 保存用户手动填写的设备标识（<c>x-rpc-device_id</c>），并立即对后续请求生效。
    /// <para>
    /// 实测服务端只认这个请求头：它等于本机**已登记**的那个值就正常，换成任何陌生值都会被判成
    /// 10041（账号存在风险）。所以这里只接受用户从自己浏览器请求头里抄来的那一枚，
    /// <b>不做任何自动获取/登记/轮换</b>；留空时调用方不应调用本方法（保持原值不动）。
    /// </para>
    /// <para>
    /// 这里**只改设备标识本身**，不触碰 <c>HyperionDeviceAndroidId</c> / 指纹 / seed。
    /// </para>
    /// </summary>
    /// <param name="deviceId">形如 <c>d5c54418-09ce-3b92-bd97-47729d496077</c> 的 GUID；空白或格式不合法时返回 false。</param>
    /// <returns>是否写入了一个格式合法的设备标识。</returns>
    public bool SaveDeviceId(string? deviceId)
    {
        deviceId = deviceId?.Trim();
        if (string.IsNullOrWhiteSpace(deviceId) || !Guid.TryParse(deviceId, out Guid parsed))
        {
            return false;
        }
        deviceId = parsed.ToString("D");
        AppConfig.HyperionDeviceId = deviceId;
        // 立即生效：两个客户端的 DeviceId 都写上（国服走 hyperion、国际服走 hoyolab）。
        _hyperionClient.DeviceId = deviceId;
        _hoyolabClient.DeviceId = deviceId;
        _logger.LogInformation("Device id (x-rpc-device_id) set to {DeviceId}.", deviceId);
        return true;
    }




    /// <summary>
    /// 保存用户指定的设备信息，并立即对后续请求生效。空白的项保持原值。
    /// </summary>
    /// <param name="deviceName">设备名。</param>
    /// <param name="systemVersion">Android 版本。</param>
    /// <param name="appVersion">米游社 App 版本。</param>
    /// <returns>是否写入了至少一项。</returns>
    public bool SaveDeviceInfo(string? deviceName, string? systemVersion, string? appVersion)
    {
        bool saved = false;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            AppConfig.HyperionDeviceName = deviceName.Trim();
            saved = true;
        }
        if (!string.IsNullOrWhiteSpace(systemVersion))
        {
            AppConfig.HyperionSystemVersion = systemVersion.Trim();
            saved = true;
        }
        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            AppConfig.HyperionAppVersion = appVersion.Trim();
            saved = true;
        }
        if (saved)
        {
            LoadDeviceInfo();
            _logger.LogInformation("Device info set to {DeviceName} / {SystemVersion} / {AppVersion}.",
                _hyperionClient.DeviceName, _hyperionClient.SystemVersion, _hyperionClient.AppVersion);
        }
        return saved;
    }




    /// <summary>
    /// 把界面上几个输入框里的文字合起来解析成设备信息。
    /// <para>
    /// 任意一栏粘贴整段请求头、UA 或者 Cookie 都能把其余几栏补全；已经单独填过的项保持原样。
    /// 调用方拿到结果后写回输入框，再交给 <see cref="SaveDeviceFp"/> 与 <see cref="SaveDeviceInfo"/>。
    /// </para>
    /// </summary>
    /// <param name="cookie">Cookie 那一栏（工具箱的输入 Cookie 弹窗才有）。</param>
    /// <param name="deviceFp">设备指纹那一栏。</param>
    /// <param name="deviceName">设备名那一栏。</param>
    /// <param name="systemVersion">Android 版本那一栏。</param>
    /// <param name="appVersion">App 版本那一栏。</param>
    public PassportDeviceInfo ParseDeviceInfoInputs(string? cookie, string? deviceFp, string? deviceName, string? systemVersion, string? appVersion)
    {
        var current = new PassportDeviceInfo
        {
            DeviceFp = NullIfWhiteSpace(deviceFp),
            DeviceName = NullIfWhiteSpace(deviceName),
            SystemVersion = NullIfWhiteSpace(systemVersion),
            AppVersion = NullIfWhiteSpace(appVersion),
        };
        var info = current.Merge(cookie, deviceFp, deviceName, systemVersion, appVersion);
        // 抓包里的 x-rpc-device_name 是百分号转义过的（例如 Pixel%205），
        // 解析出来先归一成人读的形式再回填输入框，免得它被当成机型存下来、之后每次请求再被转义一次。
        if (!string.IsNullOrWhiteSpace(info.DeviceName))
        {
            info.DeviceName = HyperionClient.NormalizeDeviceName(info.DeviceName);
        }
        return info;
    }




    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();




    /// <summary>
    /// 把设备信息从配置写到客户端，并立即生效。
    /// <para>
    /// 只有设备名、Android 版本、米游社 App 版本可覆盖；机型档案固定用仓库自带的中性档案
    /// （见 <see cref="HyperionClient.HyperionDeviceProfile.Default"/>）。实机验证过"机型身份与账号是否匹配"
    /// 不影响接口结果，所以不再把品牌/build 串等做成配置项。
    /// </para>
    /// <para>
    /// 这几项要参与请求头和 getFp 的 body，所以每次请求前都重新写一遍，改了以后不用重启。
    /// </para>
    /// </summary>
    private void LoadDeviceInfo()
    {
        _hyperionClient.DeviceName = AppConfig.HyperionDeviceName;
        _hyperionClient.SystemVersion = AppConfig.HyperionSystemVersion;
        _hyperionClient.AppVersionOverride = AppConfig.HyperionAppVersion;
        // 首次运行还没有设备 ID 时先落一个，别再让每次请求各发一个临时的。
        string? id = AppConfig.HyperionDeviceId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = HyperionClient.GenerateDeviceId();
            AppConfig.HyperionDeviceId = id;
        }
        _hyperionClient.DeviceId = id;
        // getFp 的 device_id 是 ANDROID_ID，也要在首次运行时落一个并一直沿用。
        string? androidId = AppConfig.HyperionDeviceAndroidId;
        if (string.IsNullOrWhiteSpace(androidId))
        {
            androidId = HyperionClient.GenerateAndroidId();
            AppConfig.HyperionDeviceAndroidId = androidId;
        }
        _hyperionClient.AndroidId = androidId;
        _hyperionClient.DeviceFpSeedId = AppConfig.HyperionDeviceFpSeedId ?? "";
        _hyperionClient.DeviceFpSeedTime = AppConfig.HyperionDeviceFpSeedTime ?? "";
        string? fp = AppConfig.HyperionDeviceFp;
        if (!string.IsNullOrWhiteSpace(fp))
        {
            _hyperionClient.DeviceFp = fp;
        }
    }




    /// <summary>
    /// 请求报 10041 时是否值得提示用户「这枚指纹没被账号登记过」。
    /// <para>
    /// 判据是这枚指纹为「本程序自己申请的」：只有从没申请过（首次运行还没落指纹）或申请过它
    /// （<see cref="AppConfig.HyperionDeviceFpLastUpdateTime"/> 有值）才算。用户自己粘贴进来的浏览器指纹
    /// 是会话自带的那一枚，不该再提示他去粘贴。
    /// </para>
    /// </summary>
    public static bool CanPromptAccountDeviceFp()
    {
        return string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceFp)
            || AppConfig.HyperionDeviceFpLastUpdateTime != default;
    }




    /// <summary>
    /// 当前设备信息是否还是仓库自带的默认值（用户没有覆盖过设备名 / Android 版本）。
    /// <para>
    /// 只用于在最贴切的入口（工具箱的「输入 Cookie」弹窗）说明可以粘一次抓包/UA；
    /// 与 10041 没有因果关系——实测服务端校验的是指纹有没有被账号登记过，与机型描述无关。
    /// </para>
    /// </summary>
    /// <param name="pending">即将写入的解析结果；它带了某一项时该项就不算默认，用于判断这次粘贴能否补齐。</param>
    public static bool IsDeviceProfileNeutral(PassportDeviceInfo? pending = null)
    {
        static bool Same(string? current, string expected, string? pendingValue)
        {
            // 粘贴内容里带了这一项时按它算：用户这一笔补上之后就不再是默认档案。
            if (!string.IsNullOrWhiteSpace(pendingValue))
            {
                return false;
            }
            return string.IsNullOrWhiteSpace(current) || string.Equals(current.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }
        return Same(AppConfig.HyperionDeviceName, HyperionClient.DefaultDeviceName, pending?.DeviceName)
            && Same(AppConfig.HyperionSystemVersion, HyperionClient.DefaultSystemVersion, pending?.SystemVersion);
    }




    /// <summary>
    /// 只在解析结果与当前配置不同时写入，返回是否真的改了。空白值不写。
    /// </summary>
    /// <param name="setter">写入配置的动作。</param>
    /// <param name="current">当前配置值。</param>
    /// <param name="value">解析出来的候选值；空白表示不写。</param>
    private static bool SetIfChanged(Action<string> setter, string? current, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(current?.Trim(), value, StringComparison.Ordinal))
        {
            return false;
        }
        setter(value);
        return true;
    }








    #region Game Role



    /// <summary>
    /// 写入工具箱账号行（行不存在时创建，存在时整行替换）。
    /// <para>
    /// 账号行是按 UID 索引的（<see cref="GetRecordUser"/> / <see cref="UpdateRecordUserCookie"/> 都按它查），
    /// 通行证侧认的是 <c>account_id</c>。这两个值在服务端返回异常时可能不一致，因此导入路径会传
    /// <paramref name="accountId"/> 强制对齐，避免同一个账号出现两行、之后又被通行证刷新当成"新账号"。
    /// </para>
    /// </summary>
    /// <param name="cookie">工具箱 Cookie。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="accountId">已知的米游社账号 ID；&gt; 0 时用它作为这一行的 UID，不用接口报回来的值。</param>
    /// <returns>写入的账号信息。</returns>
    public async Task<GameRecordUser> AddRecordUserAsync(string cookie, CancellationToken cancellationToken = default, long accountId = 0)
    {
        var user = await ExecuteWithGameRecordRecoveryAsync(client => client.GetGameRecordUserAsync(cookie, cancellationToken), cancellationToken);
        if (accountId > 0 && user.Uid != accountId)
        {
            user.Uid = accountId;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GameRecordUser (Uid, IsHoyolab, Nickname, Avatar, Introduce, Gender, AvatarUrl, Pendant, Cookie)
            VALUES (@Uid, @IsHoyolab, @Nickname, @Avatar, @Introduce, @Gender, @AvatarUrl, @Pendant, @Cookie);
            """, user);
        return user;
    }



    public List<GameRecordUser> GetRecordUsers()
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordUser>("SELECT * FROM GameRecordUser WHERE IsHoyolab = @IsHoyolab;", new { IsHoyolab });
        return list.ToList();
    }



    public async Task<List<GameRecordRole>> AddGameRolesAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var list = await ExecuteWithGameRecordRecoveryAsync(client => client.GetAllGameRolesAsync(cookie, cancellationToken), cancellationToken);
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        dapper.Execute("""
            INSERT OR REPLACE INTO GameRecordRole (Uid, GameBiz, Nickname, Level, Region, RegionName, Cookie, HeadIcon)
            VALUES (@Uid, @GameBiz, @Nickname, @Level, @Region, @RegionName, @Cookie, @HeadIcon);
            """, list, t);
        t.Commit();
        return list;
    }




    public List<GameRecordRole> GetGameRoles(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz;", new { gameBiz });
        return list.ToList();
    }



    public GameRecordRole? GetLastSelectGameRecordRoleOrTheFirstOne(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        var role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key LIMIT 1;
            """, new { gameBiz, key = $"last_select_game_record_role_{gameBiz}" });
        return role ??= dapper.QueryFirstOrDefault<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz LIMIT 1;", new { gameBiz });
    }



    public void SetLastSelectGameRecordRole(GameBiz gameBiz, GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key = $"last_select_game_record_role_{gameBiz}", value = role.Uid.ToString() });
    }


    public GameRecordRole? GetLastSelectGachaSyncRoleOrTheFirstOne(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        GameRecordRole? role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key LIMIT 1;
            """, new { gameBiz, key = $"last_select_gacha_sync_role_{gameBiz}" });
        if (role is not null)
        {
            return role;
        }
        role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key LIMIT 1;
            """, new { gameBiz, key = $"last_select_game_record_role_{gameBiz}" });
        if (role is not null)
        {
            dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key = $"last_select_gacha_sync_role_{gameBiz}", value = role.Uid.ToString() });
            return role;
        }
        return dapper.QueryFirstOrDefault<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz LIMIT 1;", new { gameBiz });
    }


    public void SetLastSelectGachaSyncRole(GameBiz gameBiz, GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key = $"last_select_gacha_sync_role_{gameBiz}", value = role.Uid.ToString() });
    }


    public GameRecordUser? GetGameRecordUser(GameRecordRole? role)
    {
        if (role is null)
        {
            return null;
        }
        using var dapper = DatabaseService.CreateConnection();
        return dapper.QueryFirstOrDefault<GameRecordUser>("SELECT * FROM GameRecordUser WHERE Cookie = @Cookie LIMIT 1;", new { role.Cookie });
    }



    /// <summary>
    /// 按 UID 查询米游社账号。
    /// </summary>
    public GameRecordUser? GetRecordUser(long uid, bool isHoyolab)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.QueryFirstOrDefault<GameRecordUser>("SELECT * FROM GameRecordUser WHERE Uid = @uid AND IsHoyolab = @isHoyolab LIMIT 1;", new { uid, isHoyolab });
    }



    /// <summary>
    /// 只更新账号与角色的 Cookie，不请求任何接口。
    /// 用于通行证令牌刷新后同步工具箱的鉴权数据。
    /// <para>
    /// 一个米游社账号在工具箱里可能对应多个游戏角色（不同游戏，甚至同一个游戏的多个账号），
    /// 它们的 Cookie 是同一份鉴权信息，必须一起换掉，否则刷新令牌后工具箱里会留着旧的 Cookie。
    /// 角色表里的 Uid 是游戏内 UID 而不是米游社 ID，所以这里按 Cookie 里的 account_id 对应。
    /// </para>
    /// </summary>
    public void UpdateRecordUserCookie(long uid, bool isHoyolab, string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        string? oldCookie = dapper.QueryFirstOrDefault<string>("SELECT Cookie FROM GameRecordUser WHERE Uid = @uid AND IsHoyolab = @isHoyolab LIMIT 1;", new { uid, isHoyolab });
        using var t = dapper.BeginTransaction();
        dapper.Execute("UPDATE GameRecordUser SET Cookie = @cookie WHERE Uid = @uid AND IsHoyolab = @isHoyolab;", new { uid, isHoyolab, cookie }, t);
        foreach (var role in dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole;", transaction: t))
        {
            bool sameAccount = GetCookieAccountId(role.Cookie) == uid;
            bool sameCookie = !string.IsNullOrWhiteSpace(oldCookie) && string.Equals(role.Cookie, oldCookie, StringComparison.Ordinal);
            if (sameAccount || sameCookie)
            {
                dapper.Execute("UPDATE GameRecordRole SET Cookie = @cookie WHERE GameBiz = @GameBiz AND Uid = @Uid;", new { cookie, role.GameBiz, role.Uid }, t);
            }
        }
        t.Commit();
    }




    /// <summary>
    /// 手工粘贴 Cookie 后把这一份凭据落到该账号的工具箱账号行上，而不是只留在粘贴时新建的那一行。
    /// <para>
    /// 手工粘贴与通行证登录写的是同一批位置（<c>GameRecordUser</c> / <c>GameRecordRole</c>），
    /// 通行证在登录、换票、切换账号时都会用自己换到的令牌重写工具箱 Cookie，只留下索引到的账号行里的键。
    /// 因此这里把粘贴的键合并进该账号已有的工具箱 Cookie 并写回账号行与角色行：
    /// 与粘贴同名的键以粘贴为准（服务端签发的 <c>*_v2</c> 凭据优先于换票得到的 v1 值），
    /// 粘贴里没有的键（如通行证的 <c>stoken</c> / <c>mid</c> / 设备指纹）原样保留。
    /// 合并后的账号行 Cookie 仍然是通行证 Cookie 的超集，之后 <see cref="PassportTokenSet.CreateToolboxCookie"/>
    /// 再合并也不会丢掉粘贴的键。
    /// </para>
    /// </summary>
    /// <param name="accountId">粘贴的 Cookie 里的米游社账号 ID；&lt;= 0 时退回到 <paramref name="recordUserUid"/>。</param>
    /// <param name="pastedCookie">用户粘贴的整条 Cookie；空白时不做任何事。</param>
    /// <param name="recordUserUid">粘贴时接口报回来的工具箱账号 UID；与 <paramref name="accountId"/> 不同时两行都更新。</param>
    /// <returns>
    /// 实际写入的行数：<c>AccountRows</c> 是 <c>GameRecordUser</c> 行数、<c>RoleRows</c> 是 <c>GameRecordRole</c> 行数。
    /// 本方法**只做 UPDATE，不创建任何行**（"行不存在"时两个计数都是 0，调用方据此判断要不要改用
    /// <see cref="AddRecordUserAsync"/> / <see cref="AddGameRolesAsync"/> 去建行）。
    /// </returns>
    public (int AccountRows, int RoleRows) UpdateToolboxCookieFromPaste(long accountId, string? pastedCookie, long recordUserUid = 0)
    {
        if (string.IsNullOrWhiteSpace(pastedCookie) || (accountId <= 0 && recordUserUid <= 0))
        {
            return (0, 0);
        }
        using var dapper = DatabaseService.CreateConnection();
        bool isHoyolab = IsHoyolab;
        // 粘贴的账号行可能与通行证索引的是同一行，也可能不是；两个位置都更新，避免凭据只落在其中一边。
        var userIds = new List<long>();
        if (accountId > 0)
        {
            userIds.Add(accountId);
        }
        if (recordUserUid > 0 && recordUserUid != accountId)
        {
            userIds.Add(recordUserUid);
        }
        string? storedCookie = dapper.QueryFirstOrDefault<string>(
            "SELECT Cookie FROM GameRecordUser WHERE Uid = @uid AND IsHoyolab = @isHoyolab LIMIT 1;",
            new { uid = userIds[0], isHoyolab });
        string cookie = MergeCookiePasteWins(storedCookie, pastedCookie)!;
        using var t = dapper.BeginTransaction();
        int updatedAccounts = dapper.Execute(
            "UPDATE GameRecordUser SET Cookie = @cookie WHERE IsHoyolab = @isHoyolab AND Uid IN @uids;",
            new { cookie, isHoyolab, uids = userIds }, t);
        // 角色行优先按整条 Cookie 认（刚粘贴的那条或当时读到的那条）。
        int updatedRoles = dapper.Execute(
            "UPDATE GameRecordRole SET Cookie = @cookie WHERE Cookie = @storedCookie OR Cookie = @pastedCookie;",
            new { cookie, storedCookie, pastedCookie }, t);
        if (updatedRoles == 0)
        {
            // 兜底：角色行里存的既不是刚粘贴的那条，也不是当时读到的那条（例如期间被通行证刷新过），
            // 这时按 Cookie 里的账号 ID 认行——角色表的 Uid 是游戏 UID，只能这样对应。
            long roleOwner = accountId > 0 ? accountId : userIds[0];
            foreach (GameRecordRole role in dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole;", transaction: t))
            {
                if (GetCookieAccountId(role.Cookie) == roleOwner)
                {
                    updatedRoles += dapper.Execute("UPDATE GameRecordRole SET Cookie = @cookie WHERE GameBiz = @GameBiz AND Uid = @Uid;", new { cookie, role.GameBiz, role.Uid }, t);
                }
            }
        }
        t.Commit();
        return (updatedAccounts, updatedRoles);
    }




    /// <summary>
    /// 手工粘贴的 Cookie 覆盖在已有工具箱 Cookie 之上：同名的键取粘贴的值，已有 Cookie 里其余的键保留。
    /// <para>
    /// 与 <see cref="PassportTokenSet.CreateToolboxCookie"/> 的合并方向正好相反——那里是通行证刷新的值覆盖已有键，
    /// 这里是用户自己粘贴的凭据优先，否则一次令牌刷新就会把粘贴进来的 <c>*_v2</c> 凭据换掉。
    /// 同名键会先按不区分大小写合并，因此结果里不会出现重复的键（例如两个 <c>_MHYUUID</c>）。
    /// </para>
    /// </summary>
    /// <param name="storedCookie">工具箱里已有的 Cookie；可为空。</param>
    /// <param name="pastedCookie">用户粘贴的 Cookie。</param>
    /// <returns>合并后的 Cookie；两个入参都为空时返回 null。</returns>
    private static string? MergeCookiePasteWins(string? storedCookie, string? pastedCookie)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddCookiePairs(cookies, storedCookie);
        // 后写入的覆盖先写入的：粘贴的键值优先，粘贴里没有的键由已有 Cookie 补齐。
        AddCookiePairs(cookies, pastedCookie);
        return cookies.Count == 0 ? null : string.Join("; ", cookies.Select(static x => $"{x.Key}={x.Value}"));
    }




    /// <summary>把分号分隔的 Cookie 合并进键值集合；每个条目只按第一个等号切分，同名键以后出现的为准。</summary>
    /// <param name="cookies">目标集合。</param>
    /// <param name="cookie">Cookie 字符串；可为空。</param>
    private static void AddCookiePairs(Dictionary<string, string> cookies, string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }
            string key = part[..index].Trim();
            if (key.Length > 0)
            {
                cookies[key] = part[(index + 1)..].Trim();
            }
        }
    }




    /// <summary>从工具箱 Cookie 里读出米游社账号 ID（account_id 或 luid）。读不到时返回 0。</summary>
    public static long GetCookieAccountId(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return 0;
        }
        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }
            if (part.AsSpan(0, index).Trim() is "account_id" or "ltuid")
            {
                if (long.TryParse(part.AsSpan(index + 1).Trim(), out long value))
                {
                    return value;
                }
            }
        }
        return 0;
    }



    public async Task RefreshAllGameRolesInfoAsync()
    {
        var users = GetRecordUsers();
        foreach (var user in users)
        {
            await AddRecordUserAsync(user.Cookie!);
            await AddGameRolesAsync(user.Cookie!);
        }
    }




    /// <summary>
    /// 刷新米游社账号的资料：昵称和头像。账号中心显示的就是这份资料，
    /// 每个账号一次请求，不拉取游戏角色。
    /// </summary>
    public async Task<int> RefreshMiyousheAccountInfoAsync(CancellationToken cancellationToken = default)
    {
        int count = 0;
        bool isHoyolab = IsHoyolab;
        try
        {
            // 通行证账号只在国服，工具箱资料要走国服接口。
            IsHoyolab = false;
            foreach (var user in GetRecordUsers())
            {
                if (string.IsNullOrWhiteSpace(user.Cookie))
                {
                    continue;
                }
                await AddRecordUserAsync(user.Cookie, cancellationToken);
                count++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh miyoushe account info");
        }
        finally
        {
            IsHoyolab = isHoyolab;
        }
        return count;
    }


    public async Task RefreshGameRoleInfoAsync(GameRecordRole role)
    {
        await AddRecordUserAsync(role.Cookie!);
        await AddGameRolesAsync(role.Cookie!);
    }



    public async Task UpdateGameRoleHeadIconAsync(GameRecordRole role)
    {
        string key = $"game_record_role_head_icon_{role.GameBiz}_{role.Region}_{role.Uid}";
        if (!_memoryCache.TryGetValue(key, out bool _))
        {
            role = await ExecuteWithGameRecordRecoveryAsync(client => client.UpdateGameRoleHeadIconAsync(role));
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("""
                INSERT OR REPLACE INTO GameRecordRole (Uid, GameBiz, Nickname, Level, Region, RegionName, Cookie, HeadIcon)
                VALUES (@Uid, @GameBiz, @Nickname, @Level, @Region, @RegionName, @Cookie, @HeadIcon);
                """, role);
            _memoryCache.Set(key, true, TimeSpan.FromMinutes(5));
        }
    }



    /// <summary>
    /// 删除游戏角色，返回是否删除全部账号
    /// </summary>
    /// <param name="role"></param>
    /// <returns></returns>
    public bool DeleteGameRole(GameRecordRole role)
    {
        bool deletedUser = false;
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        dapper.Execute("DELETE FROM GameRecordRole WHERE GameBiz = @GameBiz AND Uid = @Uid;", role, t);
        _logger.LogInformation("Deleted game roles with ({nickname}, {gameBiz}, {uid}).", role.Nickname, role.GameBiz, role.Uid);
        if (dapper.QueryFirstOrDefault<int>("SELECT Count(*) FROM GameRecordRole WHERE Cookie = @Cookie;", role, t) == 0)
        {
            dapper.Execute("DELETE FROM GameRecordUser WHERE Cookie = @Cookie;", role, t);
            _logger.LogInformation("Deleted all relative accounts of ({nickname}, {gameBiz}, {uid})", role.Nickname, role.GameBiz, role.Uid);
            deletedUser = true;
        }
        t.Commit();
        return deletedUser;
    }



    #endregion




    #region 刷新工具箱数据




    /// <summary>
    /// 刷新已经 pin 的国服游戏在工具箱里的全部数据。
    /// <para>
    /// 只刷新 pin 过的游戏，而且每个游戏只刷新当前选中的那个角色：
    /// 工具箱的每个项目都是一次请求，把所有游戏的所有角色一起刷新会发出太多请求。
    /// </para>
    /// </summary>
    public async Task<(int Games, int Succeeded, int Failed)> RefreshPinnedGamesToolboxDataAsync(CancellationToken cancellationToken = default)
    {
        int games = 0;
        int succeeded = 0;
        int failed = 0;
        bool isHoyolab = IsHoyolab;
        try
        {
            // 通行证账号只在国服，工具箱数据要走国服接口。
            IsHoyolab = false;
            await UpdateDeviceFpAsync(cancellationToken: cancellationToken);
            foreach (GameBiz gameBiz in GetPinnedChinaGameBizs())
            {
                var role = GetLastSelectGameRecordRoleOrTheFirstOne(gameBiz);
                if (role is null)
                {
                    continue;
                }
                games++;
                var (ok, error) = await RefreshToolboxDataAsync(role, cancellationToken);
                succeeded += ok;
                failed += error;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh pinned games toolbox data");
        }
        finally
        {
            IsHoyolab = isHoyolab;
        }
        return (games, succeeded, failed);
    }




    /// <summary>已经 pin 到待选区的国服游戏。</summary>
    private static IEnumerable<GameBiz> GetPinnedChinaGameBizs()
    {
        foreach (string str in AppConfig.SelectedGameBizs?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
        {
            if (GameBiz.TryParse(str, out GameBiz gameBiz) && gameBiz.IsChinaServer())
            {
                yield return gameBiz;
            }
        }
    }




    /// <summary>
    /// 刷新一个游戏角色在工具箱里所有能刷新的项目：各种深渊记录、月报的近三个月、模拟宇宙等。
    /// 每个项目对应一次请求，单个项目失败不影响其它项目。
    /// </summary>
    public async Task<(int Succeeded, int Failed)> RefreshToolboxDataAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        int succeeded = 0;
        int failed = 0;
        if (role is null || role.Uid <= 0 || string.IsNullOrWhiteSpace(role.Cookie))
        {
            return (0, 0);
        }
        async Task RefreshAsync(Func<Task> action)
        {
            try
            {
                await action();
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Refresh toolbox data ({gameBiz}, {uid})", role.GameBiz, role.Uid);
            }
        }
        GameBiz.TryParse(role.GameBiz, out GameBiz gameBiz);
        switch (gameBiz.Game)
        {
            case GameBiz.hk4e:
                await RefreshAsync(() => RefreshSpiralAbyssInfoAsync(role, 1, cancellationToken));
                await RefreshAsync(() => RefreshSpiralAbyssInfoAsync(role, 2, cancellationToken));
                await RefreshAsync(() => RefreshImaginariumTheaterInfoAsync(role, cancellationToken));
                await RefreshAsync(() => RefreshStygianOnslaughtInfosAsync(role, cancellationToken));
                await RefreshAsync(() => RefreshTravelersDiaryAsync(role, cancellationToken));
                break;
            case GameBiz.hkrpg:
                await RefreshAsync(() => RefreshForgottenHallInfoAsync(role, 1, cancellationToken));
                await RefreshAsync(() => RefreshForgottenHallInfoAsync(role, 2, cancellationToken));
                await RefreshAsync(() => RefreshPureFictionInfoAsync(role, 1, cancellationToken));
                await RefreshAsync(() => RefreshPureFictionInfoAsync(role, 2, cancellationToken));
                await RefreshAsync(() => RefreshApocalypticShadowInfoAsync(role, 1, cancellationToken));
                await RefreshAsync(() => RefreshApocalypticShadowInfoAsync(role, 2, cancellationToken));
                await RefreshAsync(() => RefreshStarRailChallengePeakDataAsync(role, cancellationToken));
                await RefreshAsync(() => GetSimulatedUniverseInfoAsync(role, true));
                await RefreshAsync(() => RefreshTrailblazeCalendarAsync(role, cancellationToken));
                break;
            case GameBiz.nap:
                await RefreshAsync(() => RefreshShiyuDefenseInfoAsync(role, 1, cancellationToken));
                await RefreshAsync(() => RefreshShiyuDefenseInfoAsync(role, 2, cancellationToken));
                await RefreshAsync(() => RefreshDeadlyAssaultInfoAsync(role, 1, cancellationToken));
                await RefreshAsync(() => RefreshDeadlyAssaultInfoAsync(role, 2, cancellationToken));
                await RefreshAsync(() => RefreshInterKnotReportAsync(role, cancellationToken));
                break;
        }
        return (succeeded, failed);
    }




    /// <summary>旅行者札记：当前月，以及最近三个月的月报与明细。</summary>
    private async Task RefreshTravelersDiaryAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        var summary = await GetTravelersDiarySummaryAsync(role, 0);
        foreach (int month in RecentMonths(summary?.OptionalMonth, DateTimeOffset.Now.Month, 3))
        {
            await GetTravelersDiarySummaryAsync(role, month);
            await GetTravelersDiaryDetailAsync(role, month, 1);
            await GetTravelersDiaryDetailAsync(role, month, 2);
        }
    }




    /// <summary>开拓月历：当前月，以及最近三个月的月报与明细。</summary>
    private async Task RefreshTrailblazeCalendarAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        var summary = await GetTrailblazeCalendarSummaryAsync(role, "");
        foreach (string month in RecentMonths(summary?.OptionalMonth, 3))
        {
            await GetTrailblazeCalendarSummaryAsync(role, month);
            await GetTrailblazeCalendarDetailAsync(role, month, 1);
            await GetTrailblazeCalendarDetailAsync(role, month, 2);
        }
    }




    /// <summary>绳网月报：当前月，以及最近三个月的月报与明细。</summary>
    private async Task RefreshInterKnotReportAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        var summary = await GetInterKnotReportSummaryAsync(role, "");
        foreach (string month in RecentMonths(summary?.OptionalMonth, 3))
        {
            var monthSummary = await GetInterKnotReportSummaryAsync(role, month);
            foreach (string? type in monthSummary?.MonthData?.List?.Select(x => x.DataType) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(type))
                {
                    await GetInterKnotReportDetailAsync(role, month, type);
                }
            }
        }
    }




    /// <summary>可选月份里离当前月最近的几个月，由近到远。月份只有 1-12，需要按循环距离排序。</summary>
    private static IEnumerable<int> RecentMonths(IEnumerable<int>? optionalMonth, int currentMonth, int count)
    {
        return (optionalMonth ?? []).Distinct()
                                    .OrderBy(x =>
                                    {
                                        int distance = currentMonth - x;
                                        while (distance < 0)
                                        {
                                            distance += 12;
                                        }
                                        return distance;
                                    })
                                    .Take(count);
    }




    /// <summary>可选月份（yyyyMM）里最近的几个月，由近到远。</summary>
    private static IEnumerable<string> RecentMonths(IEnumerable<string>? optionalMonth, int count)
    {
        return (optionalMonth ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderByDescending(x => x).Take(count);
    }




    #endregion




    #region Spiral Abyss


    /// <summary>
    /// 深境螺旋
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SpiralAbyssInfo> RefreshSpiralAbyssInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithGameRecordRecoveryAsync(client => client.GetSpiralAbyssInfoAsync(role, schedule), cancellationToken);
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




    #region Traveler's Diary



    public async Task<TravelersDiarySummary> GetTravelersDiarySummaryAsync(GameRecordRole role, int month = 0)
    {
        var summary = await ExecuteWithGameRecordRecoveryAsync(client => client.GetTravelsDiarySummaryAsync(role, month));
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



    /// <summary>
    /// 拉取并写入旅行札记明细。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="month">月份。</param>
    /// <param name="type">收入类型。</param>
    /// <param name="limit">单页条数。</param>
    /// <param name="forceOverwrite">
    /// 条数一致时也覆盖写入。后台自动更新用：当月数据还在滚动，条数不变但数值会变，
    /// 只按条数判断会让本地库停在半程快照。手动获取详情保持默认的按条数跳过。
    /// </param>
    /// <returns>新增（或覆盖）的条数。</returns>
    public async Task<int> GetTravelersDiaryDetailAsync(GameRecordRole role, int month, int type, int limit = 100, bool forceOverwrite = false)
    {
        var detail = await ExecuteWithGameRecordRecoveryAsync(client => client.GetTravelsDiaryDetailAsync(role, month, type, limit));
        if (detail.List is null || detail.List.Count == 0)
        {
            return 0;
        }
        var list = detail.List;
        using var dapper = DatabaseService.CreateConnection();
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM GenshinTravelersDiaryAwardItem WHERE Uid=@Uid AND Year=@Year AND Month=@Month AND Type=@Type;", list.FirstOrDefault());
        if (!forceOverwrite && existCount == list.Count)
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




    #region Imaginarium Theater



    /// <summary>
    /// 幻想真境剧诗
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task RefreshImaginariumTheaterInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var infos = await ExecuteWithGameRecordRecoveryAsync(client => client.GetImaginariumTheaterInfosAsync(role, cancellationToken), cancellationToken);
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




    #region Simulated Universe



    public async Task<SimulatedUniverseInfo> GetSimulatedUniverseInfoAsync(GameRecordRole role, bool detail)
    {
        var info = await ExecuteWithGameRecordRecoveryAsync(client => client.GetSimulatedUniverseInfoAsync(role, detail));
        if (detail)
        {
            using var dapper = DatabaseService.CreateConnection();
            using var t = dapper.BeginTransaction();
            var obj = new
            {
                role.Uid,
                info.LastRecord.Basic.ScheduleId,
                info.LastRecord.Basic.FinishCount,
                info.LastRecord.Basic.ScheduleBegin,
                info.LastRecord.Basic.ScheduleEnd,
                info.LastRecord.HasData,
                Value = JsonSerializer.Serialize(info.LastRecord, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
                INSERT OR REPLACE INTO StarRailSimulatedUniverseRecord (Uid, ScheduleId, FinishCount, ScheduleBegin, ScheduleEnd, HasData, Value)
                VALUES (@Uid, @ScheduleId, @FinishCount, @ScheduleBegin, @ScheduleEnd, @HasData, @Value);
                """, obj, t);
            obj = new
            {
                role.Uid,
                info.CurrentRecord.Basic.ScheduleId,
                info.CurrentRecord.Basic.FinishCount,
                info.CurrentRecord.Basic.ScheduleBegin,
                info.CurrentRecord.Basic.ScheduleEnd,
                info.CurrentRecord.HasData,
                Value = JsonSerializer.Serialize(info.CurrentRecord, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
                INSERT OR REPLACE INTO StarRailSimulatedUniverseRecord (Uid, ScheduleId, FinishCount, ScheduleBegin, ScheduleEnd, HasData, Value)
                VALUES (@Uid, @ScheduleId, @FinishCount, @ScheduleBegin, @ScheduleEnd, @HasData, @Value);
                """, obj, t);
            t.Commit();
        }
        return info;
    }



    public List<SimulatedUniverseRecordBasic> GetSimulatedUniverseRecordBasics(GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<SimulatedUniverseRecordBasic>("""
            SELECT ScheduleId, FinishCount, ScheduleBegin, ScheduleEnd FROM StarRailSimulatedUniverseRecord WHERE Uid=@Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid }).ToList();
    }



    public SimulatedUniverseRecord? GetSimulatedUniverseRecord(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailSimulatedUniverseRecord WHERE Uid=@Uid AND ScheduleId=@scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<SimulatedUniverseRecord>(value);
    }



    #endregion




    #region Forgotten Hall



    public async Task<ForgottenHallInfo> RefreshForgottenHallInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithGameRecordRecoveryAsync(client => client.GetForgottenHallInfoAsync(role, schedule), cancellationToken);
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.BeginTime,
            info.EndTime,
            info.StarNum,
            info.ExtraStarNum,
            info.MaxFloor,
            info.BattleNum,
            info.HasData,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailForgottenHallInfo (Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData, Value)
            VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @StarNum, @ExtraStarNum, @MaxFloor, @BattleNum, @HasData, @Value);
            """, obj);
        return info;
    }



    public List<ForgottenHallInfo> GetForgottenHallInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ForgottenHallInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ForgottenHallInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData FROM StarRailForgottenHallInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ForgottenHallInfo? GetForgottenHallInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailForgottenHallInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ForgottenHallInfo>(value);
    }



    #endregion




    #region Pure Fiction



    public async Task<PureFictionInfo> RefreshPureFictionInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithGameRecordRecoveryAsync(client => client.GetPureFictionInfoAsync(role, schedule), cancellationToken);
        if (info.ScheduleId == 0)
        {
            return info;
        }
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.BeginTime,
            info.EndTime,
            info.StarNum,
            info.ExtraStarNum,
            info.MaxFloor,
            info.BattleNum,
            info.HasData,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailPureFictionInfo (Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData, Value)
            VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @StarNum, @ExtraStarNum, @MaxFloor, @BattleNum, @HasData, @Value);
            """, obj);
        return info;
    }



    public List<PureFictionInfo> GetPureFictionInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<PureFictionInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<PureFictionInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData FROM StarRailPureFictionInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public PureFictionInfo? GetPureFictionInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailPureFictionInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<PureFictionInfo>(value);
    }



    #endregion




    #region Apocalyptic Shadow



    public async Task<ApocalypticShadowInfo> RefreshApocalypticShadowInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithGameRecordRecoveryAsync(client => client.GetApocalypticShadowInfoAsync(role, schedule), cancellationToken);
        if (info.ScheduleId == 0)
        {
            return info;
        }
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.BeginTime,
            info.EndTime,
            info.UpperBossIcon,
            info.LowerBossIcon,
            info.TierceBossIcon,
            info.StarNum,
            info.ExtraStarNum,
            info.MaxFloor,
            info.BattleNum,
            info.HasData,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailApocalypticShadowInfo (Uid, ScheduleId, BeginTime, EndTime, UpperBossIcon, LowerBossIcon, TierceBossIcon, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData, Value)
            VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @UpperBossIcon, @LowerBossIcon, @TierceBossIcon, @StarNum, @ExtraStarNum, @MaxFloor, @BattleNum, @HasData, @Value);
            """, obj);
        return info;
    }



    public List<ApocalypticShadowInfo> GetApocalypticShadowInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ApocalypticShadowInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ApocalypticShadowInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, UpperBossIcon, LowerBossIcon, TierceBossIcon, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData FROM StarRailApocalypticShadowInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ApocalypticShadowInfo? GetApocalypticShadowInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailApocalypticShadowInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ApocalypticShadowInfo>(value);
    }



    #endregion




    #region Trailblaze Calendar



    public async Task<TrailblazeCalendarSummary> GetTrailblazeCalendarSummaryAsync(GameRecordRole role, string month = "")
    {
        var summary = await ExecuteWithGameRecordRecoveryAsync(client => client.GetTrailblazeCalendarSummaryAsync(role, month));
        if (summary.MonthData is null)
        {
            return summary;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailTrailblazeCalendarMonthData (Uid, Month, CurrentHcoin, CurrentRailsPass, LastHcoin, LastRailsPass, HcoinRate, RailsRate, GroupBy)
            VALUES (@Uid, @Month, @CurrentHcoin, @CurrentRailsPass, @LastHcoin, @LastRailsPass, @HcoinRate, @RailsRate, @GroupBy);
            """, summary.MonthData);
        return summary;
    }


    public List<TrailblazeCalendarMonthData> GetTrailblazeCalendarMonthDataList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<TrailblazeCalendarMonthData>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<TrailblazeCalendarMonthData>("SELECT * FROM StarRailTrailblazeCalendarMonthData WHERE Uid = @Uid ORDER BY Month DESC;", new { role.Uid });
        return list.ToList();
    }



    /// <summary>
    /// 拉取并写入开拓月历明细。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="month">月份，形如 202609。</param>
    /// <param name="type">收入类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="forceOverwrite">条数一致时也覆盖写入，供后台自动更新覆盖滚动中的当月数据。</param>
    /// <returns>新增（或覆盖）的条数。</returns>
    public async Task<int> GetTrailblazeCalendarDetailAsync(GameRecordRole role, string month, int type, CancellationToken cancellationToken = default, bool forceOverwrite = false)
    {
        int total = (await ExecuteWithGameRecordRecoveryAsync(client => client.GetTrailblazeCalendarDetailByPageAsync(role, month, type, 1, 1), cancellationToken)).Total;
        if (total == 0)
        {
            return 0;
        }
        using var dapper = DatabaseService.CreateConnection();
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM StarRailTrailblazeCalendarDetailItem WHERE Uid = @Uid AND Month = @month AND Type = @type;", new { role.Uid, month, type });
        if (!forceOverwrite && existCount == total)
        {
            return 0;
        }
        var detail = await ExecuteWithGameRecordRecoveryAsync(client => client.GetTrailblazeCalendarDetailAsync(role, month, type));
        var list = detail.List;
        using var t = dapper.BeginTransaction();
        dapper.Execute($"DELETE FROM StarRailTrailblazeCalendarDetailItem WHERE Uid = @Uid AND Month = @Month AND Type = @Type;", list.FirstOrDefault(), t);
        dapper.Execute("""
                INSERT INTO StarRailTrailblazeCalendarDetailItem (Uid, Month, Type, Action, ActionName, Time, Number)
                VALUES (@Uid, @Month, @Type, @Action, @ActionName, @Time, @Number);
                """, list, t);
        t.Commit();
        return total - existCount;
    }



    public List<TrailblazeCalendarDetailItem> GetTrailblazeCalendarDetailItems(long uid, string month, int type)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<TrailblazeCalendarDetailItem>("SELECT * FROM StarRailTrailblazeCalendarDetailItem WHERE Uid=@uid AND Month=@month AND Type=@type ORDER BY Time;", new { uid, month, type }).ToList();
    }




    #endregion




    #region Inter Knot Report



    public async Task<InterKnotReportSummary> GetInterKnotReportSummaryAsync(GameRecordRole role, string month = "")
    {
        var summary = await ExecuteWithGameRecordRecoveryAsync(client => client.GetInterKnotReportSummaryAsync(role, month));
        using var dapper = DatabaseService.CreateConnection();
        var obj = new
        {
            summary.Uid,
            summary.DataMonth,
            Value = JsonSerializer.Serialize(summary),
        };
        dapper.Execute("""
            INSERT OR REPLACE INTO ZZZInterKnotReportSummary (Uid, DataMonth, Value) VALUES (@Uid, @DataMonth, @Value);
            """, obj);
        return summary;
    }


    public List<InterKnotReportSummary> GetInterKnotReportSummaryList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<InterKnotReportSummary>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var values = dapper.Query<string>("SELECT Value FROM ZZZInterKnotReportSummary WHERE Uid = @Uid ORDER BY DataMonth DESC;", new { role.Uid });
        return values.Select(v => JsonSerializer.Deserialize<InterKnotReportSummary>(v)!).ToList();
    }


    public InterKnotReportSummary? GetInterKnotReportSummary(InterKnotReportSummary summary)
    {
        if (summary is null)
        {
            return null;
        }
        using var dapper = DatabaseService.CreateConnection();
        string? value = dapper.QueryFirstOrDefault<string>("SELECT Value FROM ZZZInterKnotReportSummary WHERE Uid = @Uid AND DataMonth = @DataMonth LIMIT 1;", summary);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<InterKnotReportSummary>(value);
    }



    /// <summary>
    /// 拉取并写入绳网月报明细。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="month">月份，形如 202609。</param>
    /// <param name="type">数据类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="forceOverwrite">条数一致时也覆盖写入，供后台自动更新覆盖滚动中的当月数据。</param>
    /// <returns>新增（或覆盖）的条数。</returns>
    public async Task<int> GetInterKnotReportDetailAsync(GameRecordRole role, string month, string type, CancellationToken cancellationToken = default, bool forceOverwrite = false)
    {
        int total = (await ExecuteWithGameRecordRecoveryAsync(client => client.GetInterKnotReportDetailByPageAsync(role, month, type, 1, 1), cancellationToken)).Total;
        if (total == 0)
        {
            return 0;
        }
        using var dapper = DatabaseService.CreateConnection();
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM ZZZInterKnotReportDetailItem WHERE Uid = @Uid AND DataMonth = @month AND DataType = @type;", new { role.Uid, month, type });
        if (!forceOverwrite && existCount == total)
        {
            return 0;
        }
        var detail = await ExecuteWithGameRecordRecoveryAsync(client => client.GetInterKnotReportDetailAsync(role, month, type));
        var list = detail.List;
        using var t = dapper.BeginTransaction();
        dapper.Execute($"DELETE FROM ZZZInterKnotReportDetailItem WHERE Uid = @Uid AND DataMonth = @DataMonth AND DataType = @DataType;", list.FirstOrDefault(), t);
        dapper.Execute("""
                INSERT OR REPLACE INTO ZZZInterKnotReportDetailItem (Uid, Id, DataMonth, DataType, Action, Time, Number)
                VALUES (@Uid, @Id, @DataMonth, @DataType, @Action, @Time, @Number);
                """, list, t);
        t.Commit();
        return total - existCount;
    }



    public List<InterKnotReportDetailItem> GetInterKnotReportDetailItems(long uid, string month, string type)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<InterKnotReportDetailItem>("SELECT * FROM ZZZInterKnotReportDetailItem WHERE Uid=@uid AND DataMonth=@month AND DataType=@type ORDER BY Time;", new { uid, month, type }).ToList();
    }


    public async Task<ZZZGachaRecordData> GetZZZGachaRecordAsync(GameRecordRole role, int gachaType, long? endId = null, string? language = null, CancellationToken cancellationToken = default)
    {
        if (role is null)
        {
            throw new ArgumentNullException(nameof(role));
        }
        bool isHoyolab = role.GameBiz?.EndsWith("_global", StringComparison.OrdinalIgnoreCase) ?? false;
        IsHoyolab = isHoyolab;
        if (isHoyolab && !string.IsNullOrWhiteSpace(language))
        {
            // HoYoLAB 语言由请求头决定，统一通过 HoyolabClient.Language 生效。
            Language = language;
        }
        if (!isHoyolab)
        {
            await UpdateDeviceFpAsync(cancellationToken: cancellationToken);
        }
        return await ExecuteWithGameRecordRecoveryAsync(client => client.GetZZZGachaRecordAsync(role, gachaType, endId, language, cancellationToken), cancellationToken);
    }




    #endregion




    #region Shiyu Defense



    public async Task<ShiyuDefenseWrapper> RefreshShiyuDefenseInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var wrapper = await ExecuteWithGameRecordRecoveryAsync(client => client.GetShiyuDefenseInfoAsync(role, schedule), cancellationToken);
        if (wrapper.HadalVer is "v1" && wrapper.InfoV1 is not null)
        {
            var info = wrapper.InfoV1;
            if (info.HasData)
            {
                var obj = new
                {
                    role.Uid,
                    info.ScheduleId,
                    info.BeginTime,
                    info.EndTime,
                    info.Version,
                    info.HasData,
                    info.MaxRating,
                    info.MaxRatingTimes,
                    info.MaxLayer,
                    Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
                };
                using var dapper = DatabaseService.CreateConnection();
                dapper.Execute("""
                    INSERT OR REPLACE INTO ZZZShiyuDefenseInfo (Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, MaxRatingTimes, MaxLayer, Value)
                    VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @Version, @HasData, @MaxRating, @MaxRatingTimes, @MaxLayer, @Value);
                    """, obj);
            }
        }
        else if (wrapper.HadalVer is "v2" && wrapper.InfoV2 is not null)
        {
            if (wrapper.InfoV2.Brief is not null)
            {
                var info = wrapper.InfoV2;
                if (info.PassFifthFloor)
                {
                    var obj = new
                    {
                        role.Uid,
                        info.ScheduleId,
                        info.BeginTime,
                        info.EndTime,
                        info.Version,
                        info.HasData,
                        info.MaxRating,
                        info.V2Score,
                        Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
                    };
                    using var dapper = DatabaseService.CreateConnection();
                    dapper.Execute("""
                        INSERT OR REPLACE INTO ZZZShiyuDefenseInfo (Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, V2Score, Value)
                        VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @Version, @HasData, @MaxRating, @V2Score, @Value);
                        """, obj);
                }
            }
        }
        return wrapper;
    }



    public List<ShiyuDefenseInfo> GetShiyuDefenseInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ShiyuDefenseInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ShiyuDefenseInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, MaxRatingTimes, MaxLayer, V2Score FROM ZZZShiyuDefenseInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ShiyuDefenseInfoBase? GetShiyuDefenseInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        (string version, string value) = dapper.QueryFirstOrDefault<(string Version, string Value)>("""
            SELECT Version, Value FROM ZZZShiyuDefenseInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (version is "v1")
        {
            return JsonSerializer.Deserialize<ShiyuDefenseInfo>(value);
        }
        else if (version is "v2")
        {
            return JsonSerializer.Deserialize<ShiyuDefenseInfoV2>(value);
        }
        return null;
    }



    #endregion




    #region Deadly Assault



    public async Task<DeadlyAssaultInfo> RefreshDeadlyAssaultInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithGameRecordRecoveryAsync(client => client.GetDeadlyAssaultInfoAsync(role, schedule), cancellationToken);
        if (!info.HasData)
        {
            return info;
        }
        var obj = new
        {
            role.Uid,
            info.ZoneId,
            info.StartTime,
            info.EndTime,
            info.HasData,
            info.RankPercent,
            info.TotalScore,
            info.TotalStar,
            info.HasHard,
            HardTotalScore = info.HardNodes?.Sum(x => x.Score) ?? 0,
            HardTotalStar = info.HardNodes?.Sum(x => x.Star) ?? 0,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO ZZZDeadlyAssaultInfo (Uid, ZoneId, StartTime, EndTime, HasData, RankPercent, TotalScore, TotalStar, HasHard, HardTotalScore, HardTotalStar, Value)
            VALUES (@Uid, @ZoneId, @StartTime, @EndTime, @HasData, @RankPercent, @TotalScore, @TotalStar, @HasHard, @HardTotalScore, @HardTotalStar, @Value);
            """, obj);
        info.HardTotalScore = info.HardNodes?.Sum(x => x.Score) ?? 0;
        info.HardTotalStar = info.HardNodes?.Sum(x => x.Star) ?? 0;
        return info;
    }



    public List<DeadlyAssaultInfo> GetDeadlyAssaultInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<DeadlyAssaultInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<DeadlyAssaultInfo>("""
            SELECT Uid, ZoneId, StartTime, EndTime, HasData, RankPercent, TotalScore, TotalStar, HasHard, HardTotalScore, HardTotalStar FROM ZZZDeadlyAssaultInfo WHERE Uid = @Uid ORDER BY ZoneId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public DeadlyAssaultInfo? GetDeadlyAssaultInfo(GameRecordRole role, int zoneId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM ZZZDeadlyAssaultInfo WHERE Uid = @Uid And ZoneId = @zoneId LIMIT 1;
            """, new { role.Uid, zoneId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var info = JsonSerializer.Deserialize<DeadlyAssaultInfo>(value);
        if (info is not null)
        {
            info.HardTotalScore = info.HardNodes?.Sum(x => x.Score) ?? 0;
            info.HardTotalStar = info.HardNodes?.Sum(x => x.Star) ?? 0;
        }
        return info;
    }



    #endregion




    #region Daily Note



    public async Task<BH3DailyNote> GetBH3DailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(BH3DailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out BH3DailyNote? note))
        {
            note = await ExecuteWithGameRecordRecoveryAsync(client => client.GetBH3DailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }



    public async Task<GenshinDailyNote> GetGenshinDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(GenshinDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out GenshinDailyNote? note))
        {
            note = await ExecuteWithGameRecordRecoveryAsync(client => client.GetGenshinDailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }



    public async Task<StarRailDailyNote> GetStarRailDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(StarRailDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out StarRailDailyNote? note))
        {
            note = await ExecuteWithGameRecordRecoveryAsync(client => client.GetStarRailDailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }


    public async Task<ZZZDailyNote> GetZZZDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(ZZZDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out ZZZDailyNote? note))
        {
            note = await ExecuteWithGameRecordRecoveryAsync(client => client.GetZZZDailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }




    #endregion




    #region Stygian Onslaught


    public async Task<List<StygianOnslaughtInfo>> RefreshStygianOnslaughtInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var infos = await ExecuteWithGameRecordRecoveryAsync(client => client.GetStygianOnslaughtInfosAsync(role, cancellationToken), cancellationToken);
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




    #region Star Rail Challenge Peak




    public List<ChallengePeakData> GetStarRailChallengePeakDataList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ChallengePeakData>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ChallengePeakData>("""
            SELECT Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon FROM StarRailChallengePeakData WHERE Uid = @Uid ORDER BY GroupId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public async Task RefreshStarRailChallengePeakDataAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        using var dapper = DatabaseService.CreateConnection();

        var data = await ExecuteWithGameRecordRecoveryAsync(client => client.GetStarRailChallengePeakDataAsync(role, 1, cancellationToken), cancellationToken);
        if (data.ChallengePeakRecords?.Count == 1)
        {
            var record = data.ChallengePeakRecords[0];
            var obj = new
            {
                role.Uid,
                record.Group.GroupId,
                record.Group.GameVersion,
                record.BossStars,
                record.MobStars,
                BossIcon = record.BossInfo.Icon,
                Value = JsonSerializer.Serialize(data, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
                INSERT OR REPLACE INTO StarRailChallengePeakData (Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon, Value)
                VALUES (@Uid, @GroupId, @GameVersion, @BossStars, @MobStars, @BossIcon, @Value);
                """, obj);
        }

        data = await ExecuteWithGameRecordRecoveryAsync(client => client.GetStarRailChallengePeakDataAsync(role, 3, cancellationToken), cancellationToken);
        foreach (var record in data.ChallengePeakRecords.ToList())
        {
            data.ChallengePeakRecords.Clear();
            var queryData = dapper.QueryFirstOrDefault<ChallengePeakData>("""
                SELECT BossStars, MobStars FROM StarRailChallengePeakData WHERE Uid = @Uid AND GroupId = @GroupId LIMIT 1;
                """, new { role.Uid, record.Group.GroupId });
            if (queryData is null)
            {
                data.ChallengePeakRecords.Add(record);
                data.ChallengePeakBestRecordBrief = new ChallengePeakBestRecordBrief
                {
                    BossStars = record.BossStars,
                    MobStars = record.MobStars,
                };
                var obj = new
                {
                    role.Uid,
                    record.Group.GroupId,
                    record.Group.GameVersion,
                    record.BossStars,
                    record.MobStars,
                    BossIcon = record.BossInfo.Icon,
                    Value = JsonSerializer.Serialize(data, AppConfig.JsonSerializerOptions),
                };
                dapper.Execute("""
                    INSERT OR REPLACE INTO StarRailChallengePeakData (Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon, Value)
                    VALUES (@Uid, @GroupId, @GameVersion, @BossStars, @MobStars, @BossIcon, @Value);
                    """, obj);
            }
            else if (record.BossStars > queryData.BossStars || record.MobStars > queryData.MobStars)
            {
                var queryText = dapper.QueryFirstOrDefault<string>("""
                    SELECT Value FROM StarRailChallengePeakData WHERE Uid = @Uid AND GroupId = @GroupId LIMIT 1;
                    """, new { role.Uid, record.Group.GroupId });
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    var queryValue = JsonSerializer.Deserialize<ChallengePeakData>(queryText);
                    if (queryValue is not null)
                    {
                        queryValue.ChallengePeakRecords.Clear();
                        queryValue.ChallengePeakRecords.Add(record);
                        queryValue.ChallengePeakBestRecordBrief ??= new();
                        queryValue.ChallengePeakBestRecordBrief.BossStars = record.BossStars;
                        queryValue.ChallengePeakBestRecordBrief.MobStars = record.MobStars;

                        var obj = new
                        {
                            role.Uid,
                            record.Group.GroupId,
                            record.Group.GameVersion,
                            record.BossStars,
                            record.MobStars,
                            BossIcon = record.BossInfo.Icon,
                            Value = JsonSerializer.Serialize(queryValue),
                        };
                        dapper.Execute("""
                            INSERT OR REPLACE INTO StarRailChallengePeakData (Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon, Value)
                            VALUES (@Uid, @GroupId, @GameVersion, @BossStars, @MobStars, @BossIcon, @Value);
                            """, obj);
                    }
                }
            }
        }
    }



    public ChallengePeakData? GetStarRailChallengePeakData(GameRecordRole role, int groupId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var queryText = dapper.QueryFirstOrDefault<string>("""
                    SELECT Value FROM StarRailChallengePeakData WHERE Uid = @Uid AND GroupId = @groupId LIMIT 1;
                    """, new { role.Uid, groupId });
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            return JsonSerializer.Deserialize<ChallengePeakData>(queryText);
        }
        return null;
    }




    #endregion


}
