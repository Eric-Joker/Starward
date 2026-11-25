using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;

namespace Starward.GameInstall.Service;

public class HttpService
{

    /// <summary>
    /// 下载游戏文件（块、ldiff、预下载、整包）用的 HttpClient 名称，在 <c>Starward.Features.RPC.RpcRunner</c> 中注册为只走 HTTP/1.1。
    /// HTTP/2 会把所有并发下载挤在一条 TCP 连接上，多拨按连接分流时只能用到一条线；HTTP/1.1 每个并发请求各占一条连接。
    /// 2026-10 单线（约 310 Mbps）实测：一条 HTTP/2 连接只跑到 13~28 MiB/s 且时快时慢，HTTP/1.1 多条连接能跑满约 37 MiB/s，
    /// 块下载、整文件、ldiff 的速度中位数分别快约 1.3、1.5、1.6 倍
    /// </summary>
    public const string DownloadHttpClientName = "GameDownload";

    private const int MAX_ATTEMPT = 2;

    private readonly ILogger<HttpService> _logger;

    private readonly IHttpClientFactory _httpClientFactor;

    private readonly SlidingWindowRateLimiter _rateLimiter;


    public HttpService(ILogger<HttpService> logger, IHttpClientFactory httpClientFactor)
    {
        _logger = logger;
        _httpClientFactor = httpClientFactor;
        _rateLimiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            SegmentsPerWindow = 1,
            Window = TimeSpan.FromMinutes(1),
        });
    }



    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellation)
    {
        return await GetAsync(url, null, cancellation).ConfigureAwait(false);
    }


    public async Task<HttpResponseMessage> GetAsync(string url, RangeHeaderValue? range, CancellationToken cancellation)
    {
        using HttpClient httpClient = _httpClientFactor.CreateClient(DownloadHttpClientName);
        HttpResponseMessage result = null!;
        for (int i = 0; i < MAX_ATTEMPT; i++)
        {
            // 自己构造的请求不会套用 HttpClient 的默认版本，要手动带上
            HttpRequestMessage request = new(HttpMethod.Get, url) { Version = httpClient.DefaultRequestVersion, VersionPolicy = httpClient.DefaultVersionPolicy };
            request.Headers.Range = range;
            HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                response.EnsureSuccessStatusCode();
            }
            result?.Dispose();
            result = response;
            if (response.RequestMessage?.RequestUri?.IsDefaultPort ?? true)
            {
                break;
            }
            using RateLimitLease lease = _rateLimiter.AttemptAcquire();
            if (lease.IsAcquired)
            {
                await Task.Delay(300, cancellation).ConfigureAwait(false);
                continue;
            }
            break;
        }
        return result;
    }


}