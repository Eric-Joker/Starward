using Microsoft.Extensions.Logging;
using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Http.Headers;

namespace Starward.GameInstall.Service;


internal class DownloadService
{

    public const int BUFFER_SIZE = 8192;

    /// <summary>
    /// 单块或整文件下载超过这么久没收到新数据就放弃，交给文件级重试。官方启动器由 getParamsConfig 下发 request_timeout_wait_time=30。
    /// HttpClient.Timeout 只管到收到响应头为止，服务器发到一半停住又不断开时，读数据会一直等下去
    /// </summary>
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(30);


    private readonly ILogger<DownloadService> _logger;

    private readonly HttpService _httpService;



    public DownloadService(ILogger<DownloadService> logger, HttpService httpService)
    {
        _logger = logger;
        _httpService = httpService;
    }




    /// <summary>
    /// 下载文件到指定路径，支持断点续传，不含 MD5 校验
    /// </summary>
    /// <exception cref="TimeoutException">超过 <see cref="DownloadStallTimeout"/> 没有收到新数据，已写入的部分保留，重试时断点续传</exception>
    public async Task DownloadToFileAsync(string path, string url, long size, GameInstallContext? context = null, CancellationToken cancellation = default)
    {
        long progressBytes = 0;
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }
        using FileStream fs = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (fs.Length >= size)
        {
            fs.SetLength(size);
            context?.AddNetworkBytes(size);
            context?.AddStorageBytes(size);
            progressBytes = size;
        }
        else if (fs.Length < size)
        {
            // 从发出请求开始计时，只算等网络的时间，每收到一段数据重新计时
            using StallDetector stall = new(cancellation);
            try
            {
                RangeHeaderValue? range = null;
                if (fs.Length > 0)
                {
                    range = new RangeHeaderValue(fs.Length, null);
                }
                using HttpResponseMessage response = await _httpService.GetAsync(url, range, stall.Token).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.PartialContent)
                {
                    fs.Position = response.Content.Headers.ContentRange?.From ?? fs.Length;
                    context?.AddNetworkBytes(fs.Position);
                    context?.AddStorageBytes(fs.Position);
                    progressBytes += fs.Position;
                }
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                try
                {
                    int read;
                    using Stream hs = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                    while ((read = await hs.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                    {
                        // 限速排队和写盘不算停滞：这段时间暂停计时，读下一段前重新计时
                        stall.Suspend();
                        await RateLimiter.AcquireAsync(read, cancellation).ConfigureAwait(false);
                        await fs.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        context?.AddDownloadBytes(read);
                        context?.AddNetworkBytes(read);
                        context?.AddStorageBytes(read);
                        progressBytes += read;
                        stall.Resume();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) when (stall.IsStalled)
            {
                context?.AddNetworkBytes(-progressBytes);
                context?.AddStorageBytes(-progressBytes);
                // 必须转成 TimeoutException：取消异常不会被文件级重试接手，还会被当成用户暂停
                throw stall.CreateTimeoutException($"url: {url}");
            }
            catch
            {
                context?.AddNetworkBytes(-progressBytes);
                context?.AddStorageBytes(-progressBytes);
                throw;
            }
        }
    }



    /// <summary>
    /// 下载文件到指定路径，支持断点续传，不含 MD5 校验
    /// </summary>
    /// <exception cref="TimeoutException">超过 <see cref="DownloadStallTimeout"/> 没有收到新数据，已写入的部分保留，重试时断点续传</exception>
    public async Task DownloadRangeToFileAsync(string path, string url, long offset, long size, GameInstallContext? context = null, CancellationToken cancellation = default)
    {
        long progressBytes = 0;
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }
        using FileStream fs = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (fs.Length >= size)
        {
            fs.SetLength(size);
            context?.AddNetworkBytes(size);
            context?.AddStorageBytes(size);
            progressBytes = size;
        }
        else if (fs.Length < size)
        {
            // 从发出请求开始计时，只算等网络的时间，每收到一段数据重新计时
            using StallDetector stall = new(cancellation);
            try
            {
                RangeHeaderValue range = new(offset + fs.Length, offset + size - 1);
                using HttpResponseMessage response = await _httpService.GetAsync(url, range, stall.Token).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.PartialContent)
                {
                    fs.Position = response.Content.Headers.ContentRange?.From ?? fs.Length;
                    context?.AddNetworkBytes(fs.Position);
                    context?.AddStorageBytes(fs.Position);
                    progressBytes += fs.Position;
                }
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                try
                {
                    int read;
                    using Stream hs = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                    while ((read = await hs.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                    {
                        // 限速排队和写盘不算停滞：这段时间暂停计时，读下一段前重新计时
                        stall.Suspend();
                        await RateLimiter.AcquireAsync(read, cancellation).ConfigureAwait(false);
                        await fs.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        context?.AddDownloadBytes(read);
                        context?.AddNetworkBytes(read);
                        context?.AddStorageBytes(read);
                        progressBytes += read;
                        stall.Resume();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) when (stall.IsStalled)
            {
                context?.AddNetworkBytes(-progressBytes);
                context?.AddStorageBytes(-progressBytes);
                throw stall.CreateTimeoutException($"url: {url}");
            }
            catch
            {
                context?.AddNetworkBytes(-progressBytes);
                context?.AddStorageBytes(-progressBytes);
                throw;
            }
        }
    }



    /// <summary>
    /// 下载到流
    /// </summary>
    /// <exception cref="TimeoutException">超过 <see cref="DownloadStallTimeout"/> 没有收到新数据</exception>
    public async Task DownloadToStreamAsync(string url, Stream targetStream, GameInstallContext? context = null, CancellationToken cancellation = default)
    {
        long progressBytes = 0;
        using StallDetector stall = new(cancellation);
        try
        {
            using HttpResponseMessage response = await _httpService.GetAsync(url, stall.Token).ConfigureAwait(false);
            using Stream hs = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
            try
            {
                int read;
                while ((read = await hs.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    // 限速排队和写盘不算停滞：这段时间暂停计时，读下一段前重新计时
                    stall.Suspend();
                    await RateLimiter.AcquireAsync(read, cancellation).ConfigureAwait(false);
                    await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    context?.AddDownloadBytes(read);
                    context?.AddNetworkBytes(read);
                    context?.AddStorageBytes(read);
                    progressBytes += read;
                    stall.Resume();
                }
                await targetStream.FlushAsync(cancellation).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (stall.IsStalled)
        {
            context?.AddNetworkBytes(-progressBytes);
            context?.AddStorageBytes(-progressBytes);
            throw stall.CreateTimeoutException($"url: {url}");
        }
        catch
        {
            context?.AddNetworkBytes(-progressBytes);
            context?.AddStorageBytes(-progressBytes);
            throw;
        }
    }



    /// <summary>
    /// 下载并 zstd 解压到流
    /// </summary>
    /// <exception cref="TimeoutException">超过 <see cref="DownloadStallTimeout"/> 没有收到新数据</exception>
    public async Task DownloadAndDecompressZstdToStreamAsync(string url, Stream targetStream, GameInstallContext? context = null, CancellationToken cancellation = default)
    {
        long networkBytes = 0, storageBytes = 0;
        using StallDetector stall = new(cancellation);
        using HttpResponseMessage response = await _httpService.GetAsync(url, stall.Token).ConfigureAwait(false);
        using Stream hs = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        Pipe pipe = new();
        using ZstandardStream ds = new(pipe.Reader.AsStream(), CompressionMode.Decompress);

        async Task ProduceAsync()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
            try
            {
                int read;
                while ((read = await hs.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    // 限速排队和写管道不算停滞：这段时间暂停计时，读下一段前重新计时
                    stall.Suspend();
                    await RateLimiter.AcquireAsync(read, cancellation).ConfigureAwait(false);
                    await pipe.Writer.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    context?.AddDownloadBytes(read);
                    context?.AddNetworkBytes(read);
                    networkBytes += read;
                    stall.Resume();
                }
                await pipe.Writer.FlushAsync(cancellation).ConfigureAwait(false);
                await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        async Task ConsumeAsync()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
            try
            {
                int read;
                while ((read = await ds.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false)) > 0)
                {
                    await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    context?.AddStorageBytes(read);
                    storageBytes += read;
                }
                await targetStream.FlushAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        try
        {
            await Task.WhenAll(ProduceAsync(), ConsumeAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stall.IsStalled)
        {
            context?.AddNetworkBytes(-networkBytes);
            context?.AddStorageBytes(-storageBytes);
            throw stall.CreateTimeoutException($"url: {url}");
        }
        catch
        {
            context?.AddNetworkBytes(-networkBytes);
            context?.AddStorageBytes(-storageBytes);
            throw;
        }
    }



    /// <summary>
    /// 下载一个压缩块到内存。先占 <paramref name="scheduler"/> 的下载名额，读完响应才归还。
    /// </summary>
    /// <param name="url">块下载地址</param>
    /// <param name="size">压缩后的块大小</param>
    /// <param name="scheduler">本次下载共用的并发与内存限制</param>
    /// <param name="context">用于统计下载速度</param>
    /// <param name="cancellation"></param>
    /// <returns>从 <paramref name="scheduler"/> 的缓冲区池租用的缓冲区，前 <paramref name="size"/> 字节为块数据，用完须归还</returns>
    /// <exception cref="IOException">数据长度不符</exception>
    /// <exception cref="TimeoutException">超过 <see cref="DownloadStallTimeout"/> 没有收到新数据</exception>
    public async Task<byte[]> DownloadCompressedChunkToBufferAsync(string url, int size, ChunkDownloadScheduler scheduler, GameInstallContext? context = null, CancellationToken cancellation = default)
    {
        await scheduler.WaitDownloadSlotAsync(cancellation).ConfigureAwait(false);
        byte[]? buffer = null;
        // 排队等下载名额的时间不算，从发出请求开始计时
        using StallDetector stall = new(cancellation);
        try
        {
            using HttpResponseMessage response = await _httpService.GetAsync(url, stall.Token).ConfigureAwait(false);
            using Stream hs = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            buffer = scheduler.BufferPool.Rent(size);
            int total = 0;
            while (total < size)
            {
                // 每次最多读 BUFFER_SIZE：限速时令牌上限最小就是 BUFFER_SIZE，一次申请更多会抛异常
                int read = await hs.ReadAsync(buffer.AsMemory(total, Math.Min(BUFFER_SIZE, size - total)), stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                // 限速排队不算停滞：这段时间暂停计时，读下一段前重新计时
                stall.Suspend();
                await RateLimiter.AcquireAsync(read, cancellation).ConfigureAwait(false);
                total += read;
                context?.AddDownloadBytes(read);
                stall.Resume();
            }
            if (total != size)
            {
                throw new IOException($"Chunk data length not match, url: {url}, expected: {size}, received: {total}.");
            }
            return buffer;
        }
        catch (OperationCanceledException) when (stall.IsStalled)
        {
            if (buffer is not null)
            {
                scheduler.BufferPool.Return(buffer);
            }
            throw stall.CreateTimeoutException($"url: {url}");
        }
        catch
        {
            if (buffer is not null)
            {
                scheduler.BufferPool.Return(buffer);
            }
            throw;
        }
        finally
        {
            scheduler.ReleaseDownloadSlot();
        }
    }



    /// <summary>
    /// 把 <see cref="DownloadCompressedChunkToBufferAsync"/> 下载的压缩块解压写入目标流的当前位置
    /// </summary>
    public static async Task DecompressZstdBufferToStreamAsync(byte[] buffer, int size, Stream targetStream, CancellationToken cancellation = default)
    {
        using MemoryStream ms = new(buffer, 0, size, writable: false);
        using ZstandardStream ds = new(ms, CompressionMode.Decompress);
        await ds.CopyToAsync(targetStream, cancellation).ConfigureAwait(false);
    }



    /// <summary>
    /// 网络读取停滞检测。读数据的取消令牌和用户取消分开：
    /// 超过 <see cref="DownloadStallTimeout"/> 没收到新数据时取消读取并转成 <see cref="TimeoutException"/>，交给文件级重试；
    /// 限速排队和写盘不算停滞，调用方在这段时间暂停计时
    /// </summary>
    private sealed class StallDetector : IDisposable
    {

        private readonly CancellationTokenSource _cts;

        private readonly CancellationToken _cancellation;


        public StallDetector(CancellationToken cancellation)
        {
            _cancellation = cancellation;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _cts.CancelAfter(DownloadStallTimeout);
        }


        /// <summary>
        /// 网络读取用的取消令牌
        /// </summary>
        public CancellationToken Token => _cts.Token;


        /// <summary>
        /// 是否是停滞超时（而不是用户取消 / 暂停）
        /// </summary>
        public bool IsStalled => _cts.IsCancellationRequested && !_cancellation.IsCancellationRequested;


        /// <summary>
        /// 暂停计时：限速排队、写盘等本地操作不算停滞
        /// </summary>
        public void Suspend()
        {
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        }


        /// <summary>
        /// 重新开始计时
        /// </summary>
        public void Resume()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.CancelAfter(DownloadStallTimeout);
            }
        }


        public TimeoutException CreateTimeoutException(string target)
        {
            return new TimeoutException($"No data received for {DownloadStallTimeout.TotalSeconds} seconds, {target}.");
        }


        public void Dispose()
        {
            _cts.Dispose();
        }

    }



}
