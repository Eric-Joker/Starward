using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starward.GameInstall.Model;
using Starward.GameInstall.Service;
using Starward.GameInstall.Sophon;
using System.IO.Compression;

namespace Starward.GameInstall.Step;

internal class ChunkStepBase : StepBase
{

    private readonly ILogger<ChunkStepBase> _logger;

    private readonly SharedInstallLinkService _sharedInstallLinkService;

    public ChunkStepBase(GameInstallContext context, IServiceProvider serviceProvider) : base(context, serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<ChunkStepBase>>();
        _sharedInstallLinkService = serviceProvider.GetRequiredService<SharedInstallLinkService>();
    }



    public bool AddBytesIfFinished(SophonFileItem item)
    {
        if (item.IsFinished)
        {
            _context.AddNetworkBytes(item.NetworkSize);
            _context.AddStorageBytes(item.StorageSize);
            if (item.IsHardlinked)
            {
                _context.AddHardLinkBytes(item.StorageSize);
            }
            return true;
        }
        return false;
    }



    public Task DownloadSophonChunkFileAsync(SophonChunkItem item, ChunkDownloadScheduler scheduler, CancellationToken cancellation = default)
    {
        return DownloadSophonChunkFileAsync(item, scheduler, null, cancellation);
    }



    /// <summary>
    /// 以 Chunk 模式下载文件。块按偏移顺序写入 <c>.tmp</c> 文件，文件长度就是写入进度，暂停后可按块续传；
    /// 后面需要联网的块由 <paramref name="scheduler"/> 限流后提前并行下载到内存，写到该块时直接解压。
    /// </summary>
    /// <param name="item">要下载的文件</param>
    /// <param name="scheduler">本次下载共用的并发与内存限制</param>
    /// <param name="reusableChunks">可按内容复用的旧版本块，按解压后 MD5 索引；没有旧版本时为 <see langword="null"/></param>
    /// <param name="cancellation"></param>
    public async Task DownloadSophonChunkFileAsync(SophonChunkItem item, ChunkDownloadScheduler scheduler, IReadOnlyDictionary<string, (SophonChunkItem File, SophonChunk Chunk)>? reusableChunks, CancellationToken cancellation = default)
    {
        if (item.ChunkFile.Type is 0x40)
        {
            return;
        }
        if (AddBytesIfFinished(item))
        {
            return;
        }
        long storageSize = item.ChunkFile.Size;
        long networkSize = item.ChunkFile.Chunks.Sum(x => x.CompressedSize);
        string fullPath = Path.Combine(_context.InstallPath, item.ChunkFile.File);
        if (await TryHardLinkAsync(item, fullPath, networkSize, storageSize, item.ChunkFile.Md5, cancellation).ConfigureAwait(false))
        {
            return;
        }
        if (await CheckFileMD5Async(item, fullPath, networkSize, storageSize, item.ChunkFile.Md5, true, cancellation).ConfigureAwait(false))
        {
            HardLinkService.InsertGameFileItem(_context.GameId, fullPath, storageSize, item.ChunkFile.Md5);
            return;
        }

        bool countReusedChunksAsNetwork = false;
        if (reusableChunks is null && _context.Operation is GameInstallOperation.Repair && File.Exists(fullPath))
        {
            // 修复时坏文件里可能只有少数几个块损坏：把本文件自己的块也当作可复用来源，逐块校验后只重下损坏的块。
            // 修复的网络总量按文件全部块计算，复用掉的块也算作已完成，进度才能到 100%
            reusableChunks = BuildSelfReusableChunks(item);
            countReusedChunksAsNetwork = true;
        }
        await DownloadSophonChunkFileContentAsync(item, scheduler, reusableChunks, networkSize, storageSize, fullPath, countReusedChunksAsNetwork, cancellation).ConfigureAwait(false);
    }



    /// <summary>
    /// 把文件自己的块按解压后 MD5 索引，修复时作为可复用来源。实际复制前还会逐块校验内容
    /// </summary>
    private static Dictionary<string, (SophonChunkItem File, SophonChunk Chunk)> BuildSelfReusableChunks(SophonChunkItem item)
    {
        Dictionary<string, (SophonChunkItem File, SophonChunk Chunk)> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (SophonChunk chunk in item.ChunkFile.Chunks)
        {
            dict.TryAdd(chunk.UncompressedMd5, (item, chunk));
        }
        return dict;
    }



    /// <summary>
    /// 下载文件内容并按偏移顺序写入 <c>.tmp</c>，校验通过后替换正式文件。调用方已完成硬链接和整文件校验。
    /// 块由 <paramref name="scheduler"/> 限流后提前并行下载到内存，写到该块时直接解压。
    /// </summary>
    /// <param name="item">要下载的文件</param>
    /// <param name="scheduler">本次下载共用的并发与内存限制</param>
    /// <param name="reusableChunks">可按内容复用的旧版本块，按解压后 MD5 索引</param>
    /// <param name="networkSize">该文件计入网络下载量的字节数</param>
    /// <param name="storageSize">该文件写入存储的字节数</param>
    /// <param name="fullPath">目标文件路径</param>
    /// <param name="cancellation"></param>
    protected async Task DownloadSophonChunkFileContentAsync(SophonChunkItem item, ChunkDownloadScheduler scheduler, IReadOnlyDictionary<string, (SophonChunkItem File, SophonChunk Chunk)>? reusableChunks, long networkSize, long storageSize, string fullPath, bool countReusedChunksAsNetwork, CancellationToken cancellation = default)
    {
        string? folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }
        string path_tmp = fullPath + ".tmp";
        long networkBytes = 0, storageBytes = 0;
        using CancellationTokenSource prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // 已发出的预读，按块下标索引，每个占一个预读名额
        Dictionary<int, Task<byte[]>> prefetched = new();
        // 当前块的下载：预读来的，或没抢到预读名额、写到这里才开始的
        Task<byte[]>? pending = null;
        bool pendingIsPrefetched = false;
        try
        {
            using (FileStream fs = File.Open(path_tmp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                long writtenLength = fs.Length;
                List<int> networkChunks = GetNetworkChunkIndexes(item, writtenLength, reusableChunks);
                int nextNetworkChunk = 0;
                for (int i = 0; i < item.ChunkFile.Chunks.Count; i++)
                {
                    SophonChunk chunk = item.ChunkFile.Chunks[i];
                    if (writtenLength >= chunk.Offset + chunk.UncompressedSize)
                    {
                        // 之前下载中断时留下的块，按块校验后算作已完成，暂停后继续不必从头再来
                        fs.Position = chunk.Offset;
                        if (await HashService.CheckStreamMD5Async(fs, chunk.UncompressedSize, chunk.UncompressedMd5, cancellation).ConfigureAwait(false))
                        {
                            _context.AddNetworkBytes(chunk.CompressedSize);
                            _context.AddStorageBytes(chunk.UncompressedSize);
                            networkBytes += chunk.CompressedSize;
                            storageBytes += chunk.UncompressedSize;
                            continue;
                        }
                    }

                    while (nextNetworkChunk < networkChunks.Count && networkChunks[nextNetworkChunk] < i)
                    {
                        nextNetworkChunk++;
                    }
                    if (prefetched.Remove(i, out pending))
                    {
                        pendingIsPrefetched = true;
                    }
                    else if (nextNetworkChunk < networkChunks.Count && networkChunks[nextNetworkChunk] == i)
                    {
                        pending = DownloadCompressedChunkAsync(item, chunk, scheduler, prefetchCts.Token);
                        pendingIsPrefetched = false;
                        nextNetworkChunk++;
                    }

                    // 写当前块之前，把后面要联网的块先下载起来
                    while (prefetched.Count < ChunkDownloadScheduler.MaxPrefetchedChunksPerFile
                        && nextNetworkChunk < networkChunks.Count
                        && scheduler.TryEnterPrefetch())
                    {
                        int index = networkChunks[nextNetworkChunk++];
                        prefetched[index] = DownloadCompressedChunkAsync(item, item.ChunkFile.Chunks[index], scheduler, prefetchCts.Token);
                    }

                    fs.Position = chunk.Offset;
                    if (pending is not null)
                    {
                        Task<byte[]> downloadTask = pending;
                        bool holdsPrefetchSlot = pendingIsPrefetched;
                        pending = null;
                        try
                        {
                            byte[] buffer = await downloadTask.ConfigureAwait(false);
                            try
                            {
                                await DownloadService.DecompressZstdBufferToStreamAsync(buffer, checked((int)chunk.CompressedSize), fs, cancellation).ConfigureAwait(false);
                                _context.AddNetworkBytes(chunk.CompressedSize);
                                _context.AddStorageBytes(chunk.UncompressedSize);
                                networkBytes += chunk.CompressedSize;
                                storageBytes += chunk.UncompressedSize;
                            }
                            finally
                            {
                                scheduler.BufferPool.Return(buffer);
                            }
                        }
                        finally
                        {
                            if (holdsPrefetchSlot)
                            {
                                scheduler.ExitPrefetch();
                            }
                        }
                    }
                    else if (await TryWriteReusableChunkAsync(chunk, fs, reusableChunks, cancellation).ConfigureAwait(false))
                    {
                        // 从旧版本文件复用的块没有网络流量；修复时网络总量按全部块计算，复用掉的块也计入已完成
                        _context.AddStorageBytes(chunk.UncompressedSize);
                        if (countReusedChunksAsNetwork)
                        {
                            _context.AddNetworkBytes(chunk.CompressedSize);
                            networkBytes += chunk.CompressedSize;
                        }
                        storageBytes += chunk.UncompressedSize;
                    }
                    else
                    {
                        string path_chunk = Path.Join(_context.InstallPath, "chunk", chunk.Id);
                        if (await HashService.CheckFileMD5Async(path_chunk, chunk.CompressedSize, chunk.CompressedMd5, cancellation).ConfigureAwait(false))
                        {
                            // 预下载缓存里的块
                            using FileStream fs_chunk = File.OpenRead(path_chunk);
                            using ZstandardStream ds = new(fs_chunk, CompressionMode.Decompress);
                            await ds.CopyToAsync(fs, cancellation).ConfigureAwait(false);
                            _context.AddNetworkBytes(chunk.CompressedSize);
                            _context.AddStorageBytes(chunk.UncompressedSize);
                            networkBytes += chunk.CompressedSize;
                            storageBytes += chunk.UncompressedSize;
                        }
                        else
                        {
                            // 本地数据都不可用，临时联网下载，不占预读名额
                            byte[] buffer = await DownloadCompressedChunkAsync(item, chunk, scheduler, cancellation).ConfigureAwait(false);
                            try
                            {
                                await DownloadService.DecompressZstdBufferToStreamAsync(buffer, checked((int)chunk.CompressedSize), fs, cancellation).ConfigureAwait(false);
                                _context.AddNetworkBytes(chunk.CompressedSize);
                                _context.AddStorageBytes(chunk.UncompressedSize);
                                networkBytes += chunk.CompressedSize;
                                storageBytes += chunk.UncompressedSize;
                            }
                            finally
                            {
                                scheduler.BufferPool.Return(buffer);
                            }
                        }
                    }
                }
            }

            if (await CheckFileMD5Async(item, path_tmp, networkSize, storageSize, item.ChunkFile.Md5, false, cancellation).ConfigureAwait(false))
            {
                // 修复时先记下其他区服共用这个文件数据的硬链接，替换后再链接到修好的文件；
                // 替换后本区服这个路径是一个新文件，反查不到原来的硬链接了
                List<string> sharedInstallFiles = _sharedInstallLinkService.GetSharedInstallFiles(_context, fullPath, item.ChunkFile.File);
                File.Move(path_tmp, fullPath, true);
                _sharedInstallLinkService.RelinkSharedInstallFiles(_context, fullPath, sharedInstallFiles);
                HardLinkService.InsertGameFileItem(_context.GameId, fullPath, storageSize, item.ChunkFile.Md5);
                item.NetworkSize = networkSize;
                item.StorageSize = storageSize;
                item.IsFinished = true;
                return;
            }
            File.Delete(path_tmp);
            _logger.LogError("File MD5 check failed, path: {path}, md5: {md5}.", fullPath, item.ChunkFile.Md5);
            throw new FileMD5FailedException(fullPath, item.ChunkFile.Md5);
        }
        catch
        {
            _context.AddNetworkBytes(-networkBytes);
            _context.AddStorageBytes(-storageBytes);
            throw;
        }
        finally
        {
            // 出错或取消时停掉本文件剩余的下载，等它们结束后才能回滚进度，否则回滚后还会有下载字节计入
            prefetchCts.Cancel();
            if (pending is not null)
            {
                await DiscardCompressedChunkAsync(pending, pendingIsPrefetched, scheduler).ConfigureAwait(false);
            }
            foreach (Task<byte[]> task in prefetched.Values)
            {
                await DiscardCompressedChunkAsync(task, true, scheduler).ConfigureAwait(false);
            }
        }
    }



    /// <summary>
    /// 预估尚未写入的块里哪些要联网下载。本地旧文件或预下载缓存大小对得上的块先按本地处理，
    /// 写到该块时再校验内容，校验不过才临时联网下载。
    /// </summary>
    private List<int> GetNetworkChunkIndexes(SophonChunkItem item, long writtenLength, IReadOnlyDictionary<string, (SophonChunkItem File, SophonChunk Chunk)>? reusableChunks)
    {
        List<int> result = new();
        // 同一个旧文件会被很多块引用，文件长度只查一次
        Dictionary<string, long> originalFileLengths = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < item.ChunkFile.Chunks.Count; i++)
        {
            SophonChunk chunk = item.ChunkFile.Chunks[i];
            if (writtenLength >= chunk.Offset + chunk.UncompressedSize)
            {
                continue;
            }
            if (TryGetReusableChunk(chunk, reusableChunks, originalFileLengths) is not null)
            {
                continue;
            }
            FileInfo cache = new(Path.Join(_context.InstallPath, "chunk", chunk.Id));
            if (cache.Exists && cache.Length == chunk.CompressedSize)
            {
                continue;
            }
            result.Add(i);
        }
        return result;
    }



    /// <summary>
    /// 从旧版本文件里截取与新块内容相同的块写入目标文件，写入位置为 <paramref name="fs"/> 的当前位置
    /// </summary>
    private async Task<bool> TryWriteReusableChunkAsync(SophonChunk chunk, FileStream fs, IReadOnlyDictionary<string, (SophonChunkItem File, SophonChunk Chunk)>? reusableChunks, CancellationToken cancellation)
    {
        (SophonChunkItem File, SophonChunk Chunk)? source = TryGetReusableChunk(chunk, reusableChunks, null);
        if (source is null)
        {
            return false;
        }
        string path_source = Path.Join(_context.InstallPath, source.Value.File.ChunkFile.File);
        using var fs_source = new FileSliceStream(path_source, source.Value.Chunk.Offset, source.Value.Chunk.UncompressedSize);
        if (!await HashService.CheckStreamMD5Async(fs_source, chunk.UncompressedSize, chunk.UncompressedMd5, cancellation).ConfigureAwait(false))
        {
            return false;
        }
        fs_source.Position = 0;
        await fs_source.CopyToAsync(fs, cancellation).ConfigureAwait(false);
        return true;
    }



    /// <summary>
    /// 按解压后 MD5 找可以复用的旧块，并确认旧文件大小足够截取出这个块
    /// </summary>
    private (SophonChunkItem File, SophonChunk Chunk)? TryGetReusableChunk(SophonChunk chunk, IReadOnlyDictionary<string, (SophonChunkItem File, SophonChunk Chunk)>? reusableChunks, Dictionary<string, long>? originalFileLengths)
    {
        if (reusableChunks is null || !reusableChunks.TryGetValue(chunk.UncompressedMd5, out var source) || source.Chunk.UncompressedSize != chunk.UncompressedSize)
        {
            return null;
        }
        string path_source = Path.Join(_context.InstallPath, source.File.ChunkFile.File);
        if (originalFileLengths is not null)
        {
            if (!originalFileLengths.TryGetValue(path_source, out long length))
            {
                FileInfo info = new(path_source);
                length = info.Exists ? info.Length : -1;
                originalFileLengths[path_source] = length;
            }
            return length >= source.Chunk.Offset + source.Chunk.UncompressedSize ? source : null;
        }
        return File.Exists(path_source) && new FileInfo(path_source).Length >= source.Chunk.Offset + source.Chunk.UncompressedSize ? source : null;
    }



    /// <summary>
    /// 下载一个块的压缩数据到内存并校验 MD5
    /// </summary>
    private async Task<byte[]> DownloadCompressedChunkAsync(SophonChunkItem item, SophonChunk chunk, ChunkDownloadScheduler scheduler, CancellationToken cancellation)
    {
        string url = item.ManifestUrl.BuildUrl(chunk.Id);
        int size = checked((int)chunk.CompressedSize);
        byte[] buffer = await _downloadService.DownloadCompressedChunkToBufferAsync(url, size, scheduler, _context, cancellation).ConfigureAwait(false);
        try
        {
            // 写入前先校验单个块，坏块只需重试这个文件剩下的部分，不必等整个文件校验失败后从头再来
            if (!HashService.CheckBufferMD5(buffer.AsSpan(0, size), chunk.CompressedMd5))
            {
                throw new IOException($"Chunk data not match, chunk: {chunk.Id}.");
            }
            return buffer;
        }
        catch
        {
            scheduler.BufferPool.Return(buffer);
            throw;
        }
    }



    /// <summary>
    /// 丢弃一个不再写入的块下载：等它结束，归还缓冲区和预读名额
    /// </summary>
    private static async Task DiscardCompressedChunkAsync(Task<byte[]> downloadTask, bool holdsPrefetchSlot, ChunkDownloadScheduler scheduler)
    {
        try
        {
            byte[] buffer = await downloadTask.ConfigureAwait(false);
            scheduler.BufferPool.Return(buffer);
        }
        catch
        {
            // 数据已经不要了，被取消或下载失败都不影响
        }
        finally
        {
            if (holdsPrefetchSlot)
            {
                scheduler.ExitPrefetch();
            }
        }
    }



    public async Task DownloadSophonChunkAsync(SophonChunkItem item, CancellationToken cancellation = default)
    {
        if (item.Chunk is null)
        {
            return;
        }
        if (AddBytesIfFinished(item))
        {
            return;
        }
        long size = item.Chunk.CompressedSize;
        string md5 = item.Chunk.CompressedMd5;
        string fullPath = Path.Combine(_context.InstallPath, "chunk", item.Chunk.Id);


        if (await TryHardLinkAsync(item, fullPath, size, size, md5, cancellation).ConfigureAwait(false))
        {
            return;
        }

        if (await CheckFileMD5Async(item, fullPath, size, size, md5, true, cancellation).ConfigureAwait(false))
        {
            HardLinkService.InsertGameFileItem(_context.GameId, fullPath, size, md5);
            return;
        }

        string? folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }
        string path_tmp = fullPath + ".tmp";
        string url = item.ManifestUrl.BuildUrl(item.Chunk.Id);
        await _downloadService.DownloadToFileAsync(path_tmp, url, size, _context, cancellation).ConfigureAwait(false);
        if (await CheckFileMD5Async(item, path_tmp, size, size, md5, false, cancellation).ConfigureAwait(false))
        {
            File.Move(path_tmp, fullPath, true);
            HardLinkService.InsertGameFileItem(_context.GameId, fullPath, size, md5);
            return;
        }
        else
        {
            File.Delete(path_tmp);
            _context.AddNetworkBytes(-size);
            _context.AddStorageBytes(-size);
            _logger.LogError("File MD5 check failed, path: {path}, md5: {md5}.", fullPath, md5);
            throw new FileMD5FailedException(fullPath, md5);
        }
    }



    public async Task DownloadSophonDiffFileAsync(SophonPatchItem item, CancellationToken cancellation = default)
    {
        if (item.DiffFile is null)
        {
            return;
        }
        if (AddBytesIfFinished(item))
        {
            return;
        }
        long size = item.DiffFile.DiffFileSize;
        string fullPath = Path.Combine(_context.InstallPath, "ldiff", item.DiffFile.Id);

        if (await TryHardLinkAsync(item, fullPath, size, size, item.DiffFile.DiffFileMd5, cancellation).ConfigureAwait(false))
        {
            return;
        }

        if (await CheckFileMD5Async(item, fullPath, size, size, item.DiffFile.DiffFileMd5, true, cancellation).ConfigureAwait(false))
        {
            HardLinkService.InsertGameFileItem(_context.GameId, fullPath, size, item.DiffFile.DiffFileMd5);
            return;
        }

        string? folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }
        string path_tmp = fullPath + ".tmp";
        string url = item.ManifestUrl.BuildUrl(item.DiffFile.Id);
        await _downloadService.DownloadToFileAsync(path_tmp, url, size, _context, cancellation).ConfigureAwait(false);
        if (await CheckFileMD5Async(item, path_tmp, size, size, item.DiffFile.DiffFileMd5, false, cancellation).ConfigureAwait(false))
        {
            File.Move(path_tmp, fullPath, true);
            HardLinkService.InsertGameFileItem(_context.GameId, fullPath, size, item.DiffFile.DiffFileMd5);
            return;
        }
        else
        {
            File.Delete(path_tmp);
            _context.AddNetworkBytes(-size);
            _context.AddStorageBytes(-size);
            _logger.LogError("File MD5 check failed, path: {path}, md5: {md5}.", fullPath, item.DiffFile.DiffFileMd5);
            throw new FileMD5FailedException(fullPath, item.DiffFile.DiffFileMd5);
        }
    }



    protected async Task<bool> TryHardLinkAsync(SophonFileItem item, string path, long networkSize, long storageSize, string md5, CancellationToken cancellation = default)
    {
        if (await HardLinkService.TryHardLinkAsync(_context.GameId, path, storageSize, md5, cancellation).ConfigureAwait(false))
        {
            _context.AddNetworkBytes(networkSize);
            _context.AddStorageBytes(storageSize);
            _context.AddHardLinkBytes(storageSize);
            item.NetworkSize = networkSize;
            item.StorageSize = storageSize;
            item.IsFinished = true;
            item.IsHardlinked = true;
            return true;
        }
        return false;
    }



    protected async Task<bool> CheckFileMD5Async(SophonFileItem item, string path, long networkSize, long storageSize, string md5, bool addContextBytes, CancellationToken cancellation = default)
    {
        if (await HashService.CheckFileMD5Async(path, storageSize, md5, cancellation).ConfigureAwait(false))
        {
            if (addContextBytes)
            {
                _context.AddNetworkBytes(networkSize);
                _context.AddStorageBytes(storageSize);
            }
            item.NetworkSize = networkSize;
            item.StorageSize = storageSize;
            item.IsFinished = true;
            return true;
        }
        return false;
    }


    /// <summary>
    /// 删除新版本已移除的旧文件。必须在全部文件写完后调用，因为更新途中还要从这些旧文件里按内容复用块。
    /// 单个文件删不掉只记日志，不影响更新结果
    /// </summary>
    /// <param name="deleteFiles">相对安装目录的路径</param>
    /// <returns>删除的文件数</returns>
    protected int DeleteRemovedFiles(IEnumerable<string>? deleteFiles)
    {
        if (deleteFiles is null)
        {
            return 0;
        }
        int count = 0;
        string installPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_context.InstallPath)) + Path.DirectorySeparatorChar;
        foreach (string item in deleteFiles)
        {
            string path = Path.GetFullPath(Path.Join(installPath, item));
            // 清单里的路径都应在安装目录内，仍做一次防护，避免误删目录外的文件
            if (!path.StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skip deleting file out of install path: {path}.", path);
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    count++;
                }
                HardLinkService.DeleteGameFileItem(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete file removed in new version: {path}.", path);
            }
        }
        return count;
    }

}