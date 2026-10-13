using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Starward.Helpers;
using Starward.Language;
using System;
using System.Threading.Tasks;

namespace Starward.Features.MapTool;

/// <summary>
/// 地图工具的内嵌浏览器：显示官方互动地图，容器与圆角同战绩页（BBSWebBridge）。
/// </summary>
/// <remarks>
/// <para>
/// 只允许导航到官方地图域名（<c>*.mihoyo.com</c> / <c>*.hoyolab.com</c> / <c>*.hoyoverse.com</c>），
/// 其它 host 的导航与新窗口请求一律拦掉，不会交给系统浏览器或任何外部进程。
/// </para>
/// <para>
/// 地图页不需要登录态，所以这里是普通 WebView2：不写 Cookie、不注入网页桥。
/// </para>
/// </remarks>
public sealed partial class MapToolWebView : UserControl
{

    private readonly ILogger<MapToolWebView> _logger = AppConfig.GetLogger<MapToolWebView>();


    /// <summary>官方地图允许的 host 后缀。</summary>
    private static readonly string[] AllowedHostSuffixes = [".mihoyo.com", ".hoyolab.com", ".hoyoverse.com"];



    public MapToolWebView()
    {
        this.InitializeComponent();
    }



    /// <summary>当前正在显示（或正在打开）的地址，重试时用。</summary>
    private string? currentUrl;


    private bool initialized;


    private bool closed;



    /// <summary>
    /// 在当前页面里打开地址；WebView2 只在第一次调用时创建。
    /// </summary>
    /// <param name="url">官方地图地址。</param>
    public async Task NavigateAsync(string url)
    {
        try
        {
            currentUrl = url;
            // 先盖上加载状态：EnsureCoreWebView2Async 期间 WebView 还没内容，不盖会露白底。
            ShowLoading();
            await webview2.EnsureCoreWebView2Async();
            // 初始化期间页面已经离开：补一次 Close，别把 msedgewebview2.exe 留在后台。
            if (WebView2Helper.CloseIfRequested(webview2, closed))
            {
                return;
            }
            CoreWebView2 coreWebView2 = webview2.CoreWebView2;
            if (!initialized)
            {
                coreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                coreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                coreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                initialized = true;
            }
            if (!IsAllowedUrl(url))
            {
                _logger.LogWarning("Map tool url is not allowed ({Url}).", url);
                ShowError(Lang.Common_NetworkError);
                return;
            }
            coreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open map tool webview ({Url}).", url);
            ShowError(Lang.Common_WebView2ComponentInitializationFailed);
        }
    }



    /// <summary>
    /// 页面离开或窗口关闭时显式释放 <c>msedgewebview2.exe</c>：WebView2 不会自己退。
    /// </summary>
    public void Close()
    {
        closed = true;
        WebView2Helper.Close(webview2);
    }



    private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        try
        {
            if (args.Uri is "about:blank")
            {
                return;
            }
            if (!IsAllowedUrl(args.Uri))
            {
                // 地图页自己往外跳（外链、广告）时拦下来，只留在官方域名里。
                args.Cancel = true;
                _logger.LogWarning("Blocked map tool navigation to {Uri}", args.Uri);
                return;
            }
            ShowLoading();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Map tool navigation starting.");
        }
    }



    private void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            if (args.IsSuccess)
            {
                ShowContent();
            }
            else
            {
                _logger.LogWarning("Map tool navigation failed ({Status}).", args.WebErrorStatus);
                ShowError(Lang.Common_NetworkError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Map tool navigation completed.");
        }
    }



    /// <summary>
    /// 新窗口请求同样不交给系统浏览器：官方域名就在当前 WebView 里接着开，其它一律丢弃。
    /// </summary>
    private void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        try
        {
            args.Handled = true;
            if (IsAllowedUrl(args.Uri))
            {
                sender.Navigate(args.Uri);
            }
            else
            {
                _logger.LogWarning("Blocked map tool new window to {Uri}", args.Uri);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Map tool new window requested.");
        }
    }



    private void Button_Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                // 失败可能发生在 CoreWebView2 建起来之前，统一走 NavigateAsync。
                _ = NavigateAsync(currentUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry map tool webview.");
        }
    }



    /// <summary>
    /// 只有官方地图域名（含子域）允许导航。
    /// </summary>
    /// <param name="url">待检查的地址。</param>
    private static bool IsAllowedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (string suffix in AllowedHostSuffixes)
        {
            if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }



    private void ShowLoading()
    {
        Grid_Loading.Visibility = Visibility.Visible;
        Grid_Error.Visibility = Visibility.Collapsed;
        ProgressRing_Loading.IsActive = true;
    }



    private void ShowContent()
    {
        ProgressRing_Loading.IsActive = false;
        Grid_Loading.Visibility = Visibility.Collapsed;
        Grid_Error.Visibility = Visibility.Collapsed;
    }



    /// <summary>
    /// 显示可重试的出错状态，不要让 WebView 白屏。
    /// </summary>
    /// <param name="message">出错原因（取自已有的 Lang 键）。</param>
    private void ShowError(string message)
    {
        ProgressRing_Loading.IsActive = false;
        Grid_Loading.Visibility = Visibility.Collapsed;
        TextBlock_Error.Text = message;
        Grid_Error.Visibility = Visibility.Visible;
    }


}
