using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZiggyCreatures.Caching.Fusion;

namespace Starward.Helpers;


internal static class FileCache
{

    private const int RETRY_COUNT = 2;

    /// <summary>
    /// 单次下载的停滞超时：等响应头或两次收到数据之间超过此时长即判定卡死。
    /// 按停滞而非总时长计时，慢网下仍在推进的大图不会被误杀。
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private static readonly FusionCache _fusionCache;

    private static readonly HttpClient _httpClient;


    static FileCache()
    {
        _fusionCache = new FusionCache(new FusionCacheOptions
        {
            DefaultEntryOptions = new FusionCacheEntryOptions
            {
                Duration = TimeSpan.MaxValue,
                FactoryHardTimeout = TimeSpan.FromMinutes(1),
                AllowTimedOutFactoryBackgroundCompletion = false,
            },
        });
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }



    public static string? CacheFolder { get; private set; }



    public static bool Initialize(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            CacheFolder = folder;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing FileCache2: {ex.Message}");
        }
        return false;
    }



    public static async Task<string?> GetFromCacheAsync(string url, bool throwOnError = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // 磁盘缓存是所有 CachedImage 共享的写入，调用方的 token 只能用来取消「等待」，
            // 不能让 ImageEx 切换 Source 时把共享的下载一起掐断：否则大图会留下永远下不完的
            // 临时文件，并且每次显示都要重新下载。
            //
            // 另外两件事也只能在这里收口：
            // 1) 失败要抛异常。GetFromCacheOrDownloadAsync 重试用尽后是 return null!，而 FusionCache
            //    会把 null 当成「值」写进缓存（Duration = MaxValue），这个 URL 就永久变成取不到，
            //    直到 DeleteCacheFile 或重启。
            // 2) 超时后不要放弃仍在写 .tmp 的 factory。静态默认 AllowTimedOutFactoryBackgroundCompletion
            //    是 false，FusionCache 在 FactoryHardTimeout（1 分钟）到点后会放掉按 key 的锁，第二个
            //    调用就会再起一个 factory，两路撞上以 FileShare.None 打开的 .tmp → IOException。
            //    打开此开关后，第二个调用会等第一次结束并拿到结果（实测 factory 只跑一次）。
            return await _fusionCache.GetOrSetAsync(
                url,
                async _ =>
                {
                    string? file = await GetFromCacheOrDownloadAsync(url, CancellationToken.None).ConfigureAwait(false);
                    return file ?? throw new HttpRequestException($"Failed to download {url}.");
                },
                options => options.AllowTimedOutFactoryBackgroundCompletion = true,
                token: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error retrieving file from cache: {ex.Message}");
            if (throwOnError)
            {
                throw;
            }
        }
        return null;
    }



    private static async Task<string> GetFromCacheOrDownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (CacheFolder is null)
        {
            throw new DirectoryNotFoundException("Cache folder not initialized.");
        }
        string filePath = Path.Combine(CacheFolder, GetCacheFileName(url));
        if (new FileInfo(filePath) is { Exists: true, Length: > 0 })
        {
            return filePath;
        }
        for (int i = 0; i < RETRY_COUNT + 1; i++)
        {
            try
            {
                await DownloadFileAsync(url, filePath).ConfigureAwait(false);
                return filePath;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                if (i < RETRY_COUNT + 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (i + 1)), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }
        }
        return null!;
    }



    /// <summary>
    /// 下载远程文件到本地缓存路径（先写 .tmp，完成后原子替换）。
    /// </summary>
    /// <param name="url">远程资源地址。</param>
    /// <param name="path">目标缓存文件完整路径（无 .tmp 后缀）。</param>
    /// <exception cref="HttpRequestException">请求失败（含 Range 无效）或响应体为空。</exception>
    /// <exception cref="TimeoutException">超过 <see cref="StallTimeout"/> 没有收到响应头或新数据。</exception>
    /// <exception cref="IOException">读写 .tmp 或移动到正式文件失败（被占用、磁盘已满等）。</exception>
    private static async Task DownloadFileAsync(string url, string path)
    {
        string tmpPath = path + ".tmp";

        // 不使用外部 CancellationToken（控件取消 ≠ 取消缓存写入），只受停滞超时约束；每收到一段数据重新计时。
        using CancellationTokenSource stallCts = new(StallTimeout);

        // 流作用域结束并释放句柄后再 Move，避免文件占用。
        try
        {
            using (FileStream fs = File.Open(tmpPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(fs.Length, null);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stallCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentRange is { From: > 0 } range)
                {
                    fs.Position = range.From.Value;
                }
                else
                {
                    fs.Position = 0;
                    fs.SetLength(0);
                }
                using var hs = await response.Content.ReadAsStreamAsync(stallCts.Token).ConfigureAwait(false);
                // 手动拷贝而非 CopyToAsync：需要每读到一段就重置停滞计时。
                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await hs.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false)) > 0)
                    {
                        stallCts.CancelAfter(StallTimeout);
                        await fs.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                await fs.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(tmpPath, path, true);
        }
        catch (OperationCanceledException) when (stallCts.IsCancellationRequested)
        {
            // 转成 TimeoutException 交给上层重试；.tmp 保留已收到的部分，下一轮续传。
            throw new TimeoutException($"No data received for {StallTimeout.TotalSeconds} seconds: {url}");
        }
    }


    private static string GetCacheFileName(string url)
    {
        byte[] hashBytes = ArrayPool<byte>.Shared.Rent(24);
        try
        {
            ReadOnlySpan<byte> pathSpan = MemoryMarshal.AsBytes(url.AsSpan());
            XxHash64.Hash(pathSpan, hashBytes.AsSpan(0, 8));
            MD5.HashData(pathSpan, hashBytes.AsSpan(8, 16));
            return Convert.ToHexString(hashBytes.AsSpan(0, 24));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashBytes);
        }
    }


    public static async void DeleteCacheFile(string url)
    {
        try
        {
            await _fusionCache.RemoveAsync(url).ConfigureAwait(false);
            await Task.Run(() =>
            {
                if (CacheFolder is null)
                {
                    return;
                }
                string filePath = Path.Combine(CacheFolder, GetCacheFileName(url));
                DeleteFileIfExists(filePath);
                DeleteFileIfExists(filePath + ".tmp");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting cache file: {ex.Message}");
        }
    }



    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting cache file: {ex.Message}");
        }
    }



}
