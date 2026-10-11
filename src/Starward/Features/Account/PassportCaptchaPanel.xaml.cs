using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Starward.Core.Passport;
using System;
using System.Text.Json;
using System.Threading.Tasks;


namespace Starward.Features.Account;

/// <summary>
/// 人机验证面板。
/// <para>
/// 米哈游的风控会在大约连续三次登录后要求完成人机验证（返回码 -3101，并在 <c>x-rpc-aigis</c> 响应头中给出验证任务）。
/// 这里把验证码服务提供方的页面直接内嵌到登录卡片里，而不是再弹一个覆盖层，
/// 这样背景、圆角、主题都和卡片保持一致。页面只加载验证码服务提供方的脚本，不包含任何官方素材。
/// </para>
/// <para>
/// 验证码控件是固定尺寸的：控件自己把占用的区域量出来交给窗口，
/// 窗口再把自己调整成同样大小，所以卡片里既不会出现空白，控件也不会被裁掉。
/// </para>
/// </summary>
public sealed partial class PassportCaptchaPanel : UserControl
{


    private readonly ILogger<PassportCaptchaPanel> _logger = AppConfig.GetLogger<PassportCaptchaPanel>();


    /// <summary>
    /// 验证码控件的默认尺寸，也是实测出来的自然尺寸：340x386。
    /// 一开始就按它摆放，加载期间窗口和控件都不会再改变位置或高度。
    /// 容器比它矮时控件会把自己压扁，所以这个值不能比实测值更小。
    /// </summary>
    public const double InitialWidgetWidth = 340;

    public const double InitialWidgetHeight = 387;


    /// <summary>尺寸上限，验证码服务返回异常尺寸时不要把窗口撑爆。</summary>
    private const double MaxWidgetWidth = 720;

    private const double MaxWidgetHeight = 900;


    /// <summary>
    /// 卡片下边缘的圆角，由页面里的白色底板自己画（要和窗口的 WindowCornerRadius 一致）。
    /// 控件的留白不一定铺满整个视口，白色底板负责把剩下的地方补成控件那样的白色。
    /// </summary>
    private const int PageCornerRadius = 20;


    /// <summary>当前这次验证的结果，取消或失败时为 null。</summary>
    private TaskCompletionSource<PassportCaptcha?>? _completion;

    private PassportAigisHeader? _aigis;

    private bool _webViewInitialized;

    /// <summary>验证码组件加载超时，避免验证码服务不可用时一直停留在加载状态。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timeoutTimer;


    /// <summary>验证码控件当前的宽度（页面量出来的 CSS 像素，1 CSS 像素 = 1 DIP）。</summary>
    public double WidgetWidth { get; private set; } = InitialWidgetWidth;


    /// <summary>验证码控件当前的高度。</summary>
    public double WidgetHeight { get; private set; } = InitialWidgetHeight;


    /// <summary>控件尺寸变化，窗口据此调整大小。</summary>
    public event EventHandler? WidgetSizeChanged;


    /// <summary>面板内容自身需要的高度，窗口用它算总高度。</summary>
    public double ContentHeight => StackPanel_Content.ActualHeight;




    public PassportCaptchaPanel()
    {
        this.InitializeComponent();
        // 窗口要按控件大小来摆，所以在加载页面之前先给一个默认尺寸，避免卡片先塌再撑开。
        SetWidgetSize(InitialWidgetWidth, InitialWidgetHeight, notify: false);
    }




    /// <summary>
    /// 开始一次人机验证，等待用户完成后返回结果。
    /// </summary>
    public async Task<PassportCaptcha?> RunAsync(PassportAigisHeader aigis, PassportGeetestChallenge challenge)
    {
        _aigis = aigis;
        _completion = new TaskCompletionSource<PassportCaptcha?>();
        try
        {
            StackPanel_Loading.Visibility = Visibility.Visible;
            TextBlock_Error.Visibility = Visibility.Collapsed;
            StartTimeoutTimer();
            await InitializeWebViewAsync();
            string html = CreateCaptchaHtml(challenge, aigis.SessionId);
            WebView_Captcha.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize captcha webview");
            ShowError(Lang.PassportLogin_CaptchaUnavailable);
        }
        return await _completion.Task;
    }




    /// <summary>用户取消验证。</summary>
    public void Cancel()
    {
        try
        {
            WebView_Captcha.CoreWebView2?.Stop();
        }
        catch { }
        Complete(null);
    }




    private void Complete(PassportCaptcha? captcha)
    {
        StopTimeoutTimer();
        _completion?.TrySetResult(captcha);
    }




    private void StartTimeoutTimer()
    {
        try
        {
            StopTimeoutTimer();
            _timeoutTimer = DispatcherQueue.CreateTimer();
            // 只等组件加载，用户自己解题的时间不受限制。
            _timeoutTimer.Interval = TimeSpan.FromSeconds(20);
            _timeoutTimer.Tick += (_, _) =>
            {
                StopTimeoutTimer();
                ShowError(Lang.PassportLogin_CaptchaUnavailable);
            };
            _timeoutTimer.Start();
        }
        catch { }
    }




    private void StopTimeoutTimer()
    {
        try
        {
            _timeoutTimer?.Stop();
            _timeoutTimer = null;
        }
        catch { }
    }




    private async Task InitializeWebViewAsync()
    {
        await WebView_Captcha.EnsureCoreWebView2Async();
        if (_webViewInitialized)
        {
            return;
        }
        var core = WebView_Captcha.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += CoreWebView2_WebMessageReceived;
        _webViewInitialized = true;
    }




    private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            string text = args.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            // 组件已经显示出来，说明加载成功，后面的时间都交给用户。
            if (root.TryGetProperty("ready", out var ready) && ready.ValueKind is JsonValueKind.True)
            {
                StopTimeoutTimer();
                StackPanel_Loading.Visibility = Visibility.Collapsed;
                return;
            }
            // 页面量出来的控件尺寸。
            if (root.TryGetProperty("size", out var size))
            {
                JsonElement viewport = default;
                if (root.TryGetProperty("viewport", out var vp) && vp.ValueKind is JsonValueKind.Object)
                {
                    viewport = vp;
                }
                bool stable = root.TryGetProperty("stable", out var st) && st.ValueKind is JsonValueKind.True;
                ReportWidgetSize(size, viewport, stable);
                return;
            }
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not JsonValueKind.True)
            {
                string? message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                ShowError(message ?? Lang.PassportLogin_CaptchaFailed);
                return;
            }
            if (!root.TryGetProperty("data", out var data))
            {
                ShowError(Lang.PassportLogin_CaptchaFailed);
                return;
            }
            StackPanel_Loading.Visibility = Visibility.Collapsed;
            Complete(ParseCaptcha(data, _aigis?.SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handle captcha result");
            ShowError(Lang.PassportLogin_CaptchaFailed);
        }
    }




    /// <summary>页面报告控件尺寸：等它稳定下来再应用。</summary>
    private void ReportWidgetSize(JsonElement size, JsonElement viewport, bool stable)
    {
        if (!size.TryGetProperty("w", out var w) || w.ValueKind is not JsonValueKind.Number ||
            !size.TryGetProperty("h", out var h) || h.ValueKind is not JsonValueKind.Number)
        {
            return;
        }
        double width = w.GetDouble();
        double height = h.GetDouble();
        if (width <= 0 || height <= 0)
        {
            return;
        }
        double viewportHeight = viewport.ValueKind is JsonValueKind.Object && viewport.TryGetProperty("h", out var vh) && vh.ValueKind is JsonValueKind.Number ? vh.GetDouble() : 0;
        double viewportWidth = viewport.ValueKind is JsonValueKind.Object && viewport.TryGetProperty("w", out var vw) && vw.ValueKind is JsonValueKind.Number ? vw.GetDouble() : 0;
        _logger.LogInformation("Captcha widget reports {Width}x{Height}, viewport {ViewportWidth}x{ViewportHeight}{Stable}",
                               width, height, viewportWidth, viewportHeight, stable ? " (stable)" : "");
        if (!stable)
        {
            // 尺寸还在变，先不动窗口。
            return;
        }
        ApplyWidgetSize(width, height);
    }




    private void ApplyWidgetSize(double width, double height)
    {
        width = Math.Clamp(width, 160, MaxWidgetWidth);
        height = Math.Clamp(height, 120, MaxWidgetHeight);
        SetWidgetSize(width, height, notify: false);
        // 尺寸没变也要通知一次：窗口第一次算高度时布局可能还没完成，需要在这里重算。
        WidgetSizeChanged?.Invoke(this, EventArgs.Empty);
    }




    /// <summary>把 WebView 调整到正好等于验证码控件本身的大小。</summary>
    private void SetWidgetSize(double width, double height, bool notify)
    {
        try
        {
            if (!double.IsNaN(WebView_Captcha.Width) &&
                Math.Abs(WebView_Captcha.Width - width) < 0.5 &&
                Math.Abs(WebView_Captcha.Height - height) < 0.5)
            {
                return;
            }
            WebView_Captcha.Width = width;
            WebView_Captcha.Height = height;
            WidgetWidth = width;
            WidgetHeight = height;
            _logger.LogInformation("Captcha webview sized to {Width}x{Height}", width, height);
            if (notify)
            {
                WidgetSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Set captcha widget size");
        }
    }




    private static PassportCaptcha ParseCaptcha(JsonElement data, string? sessionId)
    {
        string? Get(string name)
        {
            if (data.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }
        var captcha = new PassportCaptcha { SessionId = sessionId };
        string? lotNumber = Get("lot_number");
        if (!string.IsNullOrWhiteSpace(lotNumber))
        {
            captcha.GeetestV4Data = new PassportGeetestV4Data
            {
                LotNumber = lotNumber,
                CaptchaOutput = Get("captcha_output"),
                PassToken = Get("pass_token"),
                GenTime = Get("gen_time"),
                CaptchaId = Get("captcha_id"),
                SignToken = Get("sign_token"),
            };
        }
        else
        {
            captcha.GeetestChallenge = Get("geetest_challenge");
            captcha.GeetestValidate = Get("geetest_validate");
            captcha.GeetestSeccode = Get("geetest_seccode");
        }
        return captcha;
    }




    /// <summary>验证失败：给出提示并结束这次验证，让调用方继续走后面的流程。</summary>
    private void ShowError(string message)
    {
        try
        {
            StopTimeoutTimer();
            StackPanel_Loading.Visibility = Visibility.Collapsed;
            TextBlock_Error.Text = message;
            TextBlock_Error.Visibility = Visibility.Visible;
        }
        catch { }
        Complete(null);
    }




    /// <summary>
    /// 生成验证码页面。根据风控返回的任务类型选择第四代或第三代极验脚本。
    /// </summary>
    private static string CreateCaptchaHtml(PassportGeetestChallenge challenge, string? sessionId)
    {
        string gt = JsonSerializer.Serialize(challenge.Gt ?? "");
        string challengeText = JsonSerializer.Serialize(challenge.Challenge ?? "");
        string session = JsonSerializer.Serialize(sessionId ?? "");
        string language = JsonSerializer.Serialize("zh-cn");
        bool useV4 = challenge.UseV4 || string.IsNullOrWhiteSpace(challenge.Challenge);
        string script = useV4
            ? $$"""
                <script src="https://static.geetest.com/v4/gt4.js"></script>
                <script>
                initGeetest4({ captchaId: {{gt}}, product: "bind", language: {{language}}, protocol: "https://", userInfo: JSON.stringify({ session_id: {{session}} }) }, function (captcha) {
                    captcha.onSuccess(function () {
                        var result = captcha.getValidate() || {};
                        result.captcha_id = {{gt}};
                        post({ ok: true, data: result });
                    });
                    captcha.onReady(function () { post({ ready: true }); });
                    captcha.onError(function (error) { post({ ok: false, message: "验证码加载失败" }); });
                    if (captcha.onClose) { captcha.onClose(function () { post({ ok: false, message: "已取消验证" }); }); }
                    // product: "bind" 只插入容器，实测必须再调用 showCaptcha() 才会显示。
                    captcha.appendTo(document.getElementById("captcha"));
                    if (typeof captcha.showCaptcha === "function") { captcha.showCaptcha(); }
                    post({ ready: true });
                });
                </script>
                """
            : $$"""
                <script src="https://static.geetest.com/static/js/gt.0.4.9.js"></script>
                <script>
                initGeetest({ gt: {{gt}}, challenge: {{challengeText}}, offline: false, new_captcha: true, product: "float" }, function (captcha) {
                    captcha.onSuccess(function () {
                        post({ ok: true, data: captcha.getValidate() || {} });
                    });
                    captcha.onReady(function () { post({ ready: true }); });
                    captcha.onError(function (error) { post({ ok: false, message: "验证码加载失败" }); });
                    captcha.onClose(function () { post({ ok: false, message: "已取消验证" }); });
                    captcha.appendTo(document.getElementById("captcha"));
                    post({ ready: true });
                });
                </script>
                """;
        return WrapPage("", script);
    }




    /// <summary>
    /// 把验证码脚本包进页面。
    /// <para>
    /// 页面本身透明，验证码控件自己那块白底由它自己画；控件没铺满视口的部分（实测会差二十几像素）
    /// 用一块同样白色、下边缘带圆角的底板补齐，这样卡片底部不会露出窗口的背景色。
    /// 另外把控件自带的全屏遮罩（geetest_popup_ghost，30% 黑）去掉，否则那块补白会被它压暗。
    /// </para>
    /// </summary>
    private static string WrapPage(string inner, string script)
    {
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <style>
                    html, body { margin: 0; padding: 0; overflow: hidden; background: transparent;
                                 font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif; }
                    #backdrop { position: fixed; left: 0; top: 0; right: 0; bottom: 0; z-index: -1; background: #FFFFFF;
                                border-bottom-left-radius: {{PageCornerRadius}}px;
                                border-bottom-right-radius: {{PageCornerRadius}}px; }
                    #captcha { margin: 0; padding: 0; }
                    /* 控件往页面里塞的全屏遮罩，只用于它自己的弹窗形态，这里会压暗补白 */
                    [class*="geetest_popup_ghost"] { background: transparent !important; }
                </style>
                <script>
                    function post(payload) {
                        try { window.chrome.webview.postMessage(JSON.stringify(payload)); } catch (e) { }
                    }
                    // 验证码控件的根节点：第四代叫 geetest_box，第三代叫 geetest_panel / geetest_widget。
                    // 类名后面带实例编号，比较之前先去掉。
                    function elementClass(el) {
                        return typeof el.className === 'string' ? el.className : '';
                    }
                    function panelNames(el) {
                        var tokens = elementClass(el).split(/\s+/);
                        var names = [];
                        for (var i = 0; i < tokens.length; i++) {
                            var name = tokens[i].replace(/_\d+$/, '');
                            if (name === 'geetest_box' || name === 'geetest_panel' || name === 'geetest_widget') { names.push(name); }
                        }
                        return names;
                    }
                    // 控件会往页面里塞一个占满视口的遮罩（geetest_popup_ghost），量尺寸时不能算进去。
                    function isViewportMask(el) {
                        var cls = elementClass(el);
                        return cls.indexOf('ghost') >= 0 || cls.indexOf('popup') >= 0;
                    }
                    // 控件是「盒子 + 各种包装层」的结构，包装层常常是透明的并且撑满视口。
                    // 只按类名取可能取到空包装层（实测就取到过一个 387 高的透明层，真正的白色面板只有 364），
                    // 所以优先选背景不透明、且没有撑满视口的那个。
                    function isOpaque(el) {
                        try {
                            var bg = window.getComputedStyle(el).backgroundColor || '';
                            var m = bg.match(/rgba?\(([^)]+)\)/);
                            if (!m) { return false; }
                            var parts = m[1].split(',');
                            if (parts.length < 4) { return true; }
                            return parseFloat(parts[3]) >= 0.5;
                        } catch (e) { return false; }
                    }
                    function widgetRect() {
                        var nodes = document.querySelectorAll('body *');
                        var order = ['geetest_box', 'geetest_panel', 'geetest_widget'];
                        for (var k = 0; k < order.length; k++) {
                            var best = null;
                            for (var i = 0; i < nodes.length; i++) {
                                var el = nodes[i];
                                if (panelNames(el).indexOf(order[k]) < 0) { continue; }
                                var rect = el.getBoundingClientRect();
                                if (rect.width <= 0 || rect.height <= 0) { continue; }
                                if (rect.width >= window.innerWidth - 1 && rect.height >= window.innerHeight - 1) { continue; }
                                if (!isOpaque(el)) { continue; }
                                if (!best || rect.height > best.height) { best = rect; }
                            }
                            if (best) { return best; }
                        }
                        return null;
                    }
                    function measure() {
                        try {
                            var width = 0, height = 0;
                            var box = widgetRect();
                            if (box) {
                                width = box.width;
                                height = box.height;
                            }
                            else {
                                // 认不出控件根节点时退化成量子元素的并集，同样排除遮罩和补白底板。
                                var nodes = document.querySelectorAll('body *');
                                for (var i = 0; i < nodes.length; i++) {
                                    if (isViewportMask(nodes[i]) || nodes[i].id === 'backdrop') { continue; }
                                    var rect = nodes[i].getBoundingClientRect();
                                    if (rect.width > 0 && rect.height > 0) {
                                        if (rect.right > width) { width = rect.right; }
                                        if (rect.bottom > height) { height = rect.bottom; }
                                    }
                                }
                            }
                            width = Math.ceil(width);
                            height = Math.ceil(height);
                            if (width <= 0 || height <= 0) { return; }
                            var viewportWidth = window.innerWidth, viewportHeight = window.innerHeight;
                            if (width === lastWidth && height === lastHeight && viewportHeight === lastViewportHeight) {
                                // 连续两次一样才算稳定：控件有入场动画，中间值不能用来定窗口大小。
                                if (stableWidth !== width || stableHeight !== height) {
                                    stableWidth = width;
                                    stableHeight = height;
                                    post({ size: { w: width, h: height }, viewport: { w: viewportWidth, h: viewportHeight, dpr: window.devicePixelRatio }, stable: true });
                                }
                                return;
                            }
                            lastWidth = width;
                            lastHeight = height;
                            lastViewportHeight = viewportHeight;
                            stableWidth = -1;
                            stableHeight = -1;
                            post({ size: { w: width, h: height }, viewport: { w: viewportWidth, h: viewportHeight, dpr: window.devicePixelRatio } });
                        } catch (e) { }
                    }
                    var lastWidth = 0, lastHeight = 0, lastViewportHeight = 0, stableWidth = -1, stableHeight = -1;
                    setInterval(measure, 400);
                    window.addEventListener("load", measure);
                </script>
                {{script}}
            </head>
            <body>
                <div id="backdrop"></div>
                <div id="captcha">{{inner}}</div>
            </body>
            </html>
            """;
    }




}
