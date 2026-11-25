using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starward.Core.HoYoPlay;
using Starward.GameInstall.Model;
using Starward.GameInstall.Service;
using Starward.GameInstall.Sophon;
using System.IO.Compression;

namespace Starward.GameInstall.Step;


internal class ChunkUpdateStep : ChunkStepBase
{

    private readonly ILogger<ChunkUpdateStep> _logger;

    public ChunkUpdateStep(GameInstallContext context, IServiceProvider serviceProvider) : base(context, serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<ChunkUpdateStep>>();
    }


    private GameBranch _branch;

    private List<SophonPatchItem>? _patchItems;

    private List<SophonChunkItem>? _chunkItems;

    private Dictionary<string, SophonChunkItem>? _oldFileDict;

    /// <summary>
    /// 旧版本文件里的块，按解压后 MD5 索引。块的 id 是压缩后内容的 MD5，压缩参数变化时同一份内容会得到不同的 id，
    /// 按解压后的内容找才能在版本差异不大时复用更多旧块
    /// </summary>
    private Dictionary<string, (SophonChunkItem File, SophonChunk Chunk)>? _oldChunkDict;

    private List<string>? _deleteFiles;

    /// <summary>
    /// 新版本的全部文件（lDiff 清单里包括没有变化、不需要补丁的文件），删除补丁用过的原文件时不能删这些
    /// </summary>
    private HashSet<string>? _patchNewFiles;

    /// <summary>
    /// ldiff 合并成功后不再使用的原文件。等全部文件合并完再删：同一个原文件可能还要给其他文件合并
    /// </summary>
    private readonly HashSet<string> _patchOriginalFiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _patchOriginalFilesLock = new();

    /// <summary>
    /// 下载阶段已经落在本地的 ldiff 分段文件，合并阶段直接使用，不重复下载
    /// </summary>
    private readonly Dictionary<SophonPatchItem, string> _patchDiffOffsetFiles = new();

    private readonly Lock _patchDiffOffsetFilesLock = new();



    public override async Task PrepareAsync(CancellationToken cancellation = default)
    {
        if (_context.GameConfig.DefaultDownloadMode is not DownloadMode.DOWNLOAD_MODE_CHUNK)
        {
            _logger.LogWarning("Not support download mode DOWNLOAD_MODE_CHUNK.");
            throw new NotSupportedException($"Game ({_context.GameId.Id}, {_context.GameId.GameBiz}) not support download mode DOWNLOAD_MODE_CHUNK.");
        }

        string localVersion = _context.ConfigIni.GameVersion?.ToString() ?? "";
        _branch ??= (await _hoYoPlayService.GetGameBranchAsync(_context.GameId, cancellation).ConfigureAwait(false)).EnsureNotNull(_context.GameId);

        if (_branch.Main.DiffTags.Contains(localVersion))
        {
            (_patchItems, _deleteFiles, HashSet<string> newFiles) = await _gameResourceService.GetSophonPatchItemsAsync(_context.GameId, _branch.Main, _context.InstallPath, _context.CategoryScenario, _context.AudioLanguage, localVersion, cancellation).ConfigureAwait(false);
            _patchNewFiles = newFiles.Select(x => Path.GetFullPath(Path.Join(_context.InstallPath, x))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Chunk update uses patch of version {version}, patch count: {count}, delete count: {deleteCount}.", localVersion, _patchItems.Count, _deleteFiles.Count);
        }
        else
        {
            List<SophonChunkItem> oldChunkItems = await _gameResourceService.GetSophonChunkItemsAsync(_context.GameId, _branch.Main, _context.InstallPath, _context.CategoryScenario, _context.AudioLanguage, localVersion, cancellation).ConfigureAwait(false);
            // Windows 路径不区分大小写：只改了大小写的文件仍是同一个文件，不能当成「新版本已移除」而删掉
            Dictionary<string, SophonChunkItem> oldFileDict = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (SophonChunkItem File, SophonChunk Chunk)> oldChunkDict = new(StringComparer.OrdinalIgnoreCase);
            foreach (SophonChunkItem chunkItem in oldChunkItems)
            {
                oldFileDict.TryAdd(chunkItem.ChunkFile.File, chunkItem);
                foreach (SophonChunk? chunk in chunkItem.ChunkFile.Chunks)
                {
                    // 旧版本有、新版本已移除的文件也在这里：它们要等全部文件写完才删除，更新途中仍可按内容复用其中的块。
                    // 同一个块被多个文件引用时保留先出现的那个即可，内容相同
                    oldChunkDict.TryAdd(chunk.UncompressedMd5, (chunkItem, chunk));
                }
            }
            _oldFileDict = oldFileDict;
            _oldChunkDict = oldChunkDict;
            var chunkItems = await _gameResourceService.GetSophonChunkItemsAsync(_context.GameId, _branch.Main, _context.InstallPath, _context.CategoryScenario, _context.AudioLanguage, null, cancellation).ConfigureAwait(false);
            _chunkItems = new();
            foreach (SophonChunkItem item in chunkItems)
            {
                if (oldFileDict.TryGetValue(item.ChunkFile.File, out var value) && string.Equals(value.ChunkFile.Md5, item.ChunkFile.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                _chunkItems.Add(item);
            }
            HashSet<string> newFiles = chunkItems.Select(x => x.ChunkFile.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _deleteFiles = oldChunkItems.Select(x => x.ChunkFile.File).Where(x => !newFiles.Contains(x)).ToList();
            _logger.LogInformation("Chunk update uses chunk, new file count: {count}, delete count: {deleteCount}.", _chunkItems.Count, _deleteFiles.Count);
        }
    }



    protected override async Task ExecuteInternalAsync(CancellationToken cancellation = default)
    {
        await MoveCachedResourceAsync(cancellation).ConfigureAwait(false);

        if (_patchItems is not null)
        {
            await PatchUpdateAsync(cancellation).ConfigureAwait(false);
        }
        if (_chunkItems is not null)
        {
            await ChunkUpdateAsync(cancellation).ConfigureAwait(false);
        }
    }



    private async Task PatchUpdateAsync(CancellationToken cancellation = default)
    {
        if (_patchItems is null)
        {
            return;
        }

        _context.ClearProgress();
        _context.Step = RunnerStep.Patching;
        _context.ProgressDisplay = ProgressDisplay.NetworkSpeed | ProgressDisplay.NetworkBytes | ProgressDisplay.StorageBytes;
        _context.NetworkTotalBytes = _patchItems.Sum(x => x.DiffFile.DiffLength);
        _context.StorageTotalBytes = _patchItems.Sum(x => x.PatchFile.Size);
        _logger.LogInformation("Start patching {count} files, network: {networkBytes} bytes, storage: {storageBytes} bytes.", _patchItems.Count, _context.NetworkTotalBytes, _context.StorageTotalBytes);

        // 补丁下载主要等网络，文件数用官方 ldiff 的固定并发数，不跟随 CPU 核数，也不和后面的合并抢磁盘
        await PreparePatchDiffsAsync(cancellation).ConfigureAwait(false);

        Lock failedLock = new();
        List<SophonPatchItem> failedItems = new();
        // 合并要同时读写本地文件，瓶颈在磁盘：固态硬盘按 CPU 核数限流，机械硬盘只开少量并发
        ParallelOptions mergeOptions = new() { MaxDegreeOfParallelism = GetLocalFileParallelism(OfficialLdiffParallelism), CancellationToken = cancellation };
        await _polly.ForEachAsync(_patchItems, mergeOptions, async (item, token) =>
        {
            if (AddBytesIfFinished(item))
            {
                return;
            }
            if (!await TryPatchFileAsync(item, token).ConfigureAwait(false))
            {
                lock (failedLock)
                {
                    failedItems.Add(item);
                }
            }
        }).ConfigureAwait(false);

        if (failedItems.Count > 0)
        {
            _logger.LogWarning("{count} files failed to patch, download them in mode chunk.", failedItems.Count);
            await RedownloadFailedPatchFilesAsync(failedItems, cancellation).ConfigureAwait(false);
        }
    }



    /// <summary>
    /// 先把补丁需要的 ldiff 数据下载到本地，并顺便复用已存在且校验通过的游戏文件。
    /// 下载主要等网络，并发数用官方 ldiff 的固定值，不跟随 CPU 核数，也不和后面的合并抢磁盘。
    /// </summary>
    private async Task PreparePatchDiffsAsync(CancellationToken cancellation = default)
    {
        if (_patchItems is null)
        {
            return;
        }
        ParallelOptions options = new() { MaxDegreeOfParallelism = OfficialLdiffParallelism, CancellationToken = cancellation };
        await _polly.ForEachAsync(_patchItems, options, async (item, token) =>
        {
            if (AddBytesIfFinished(item))
            {
                return;
            }
            string path_target = Path.Join(_context.InstallPath, item.PatchFile.File);
            long networkSize = item.DiffFile.DiffLength, storageSize = item.PatchFile.Size;
            string md5 = item.PatchFile.Md5;
            if (await TryHardLinkAsync(item, path_target, networkSize, storageSize, md5, token).ConfigureAwait(false))
            {
                AddPatchOriginalFile(item, path_target);
                // 进度已在这里计入，合并阶段不能再计一次
                item.NetworkSize = 0;
                item.StorageSize = 0;
                return;
            }
            if (await CheckFileMD5Async(item, path_target, networkSize, storageSize, md5, true, token).ConfigureAwait(false))
            {
                HardLinkService.InsertGameFileItem(_context.GameId, path_target, storageSize, md5);
                AddPatchOriginalFile(item, path_target);
                // 进度已在这里计入，合并阶段不能再计一次
                item.NetworkSize = 0;
                item.StorageSize = 0;
                return;
            }
            string path_diff = Path.Join(_context.InstallPath, "ldiff", item.DiffFile.Id);
            if (await HashService.CheckFileMD5Async(path_diff, item.DiffFile.DiffFileSize, item.DiffFile.DiffFileMd5, token).ConfigureAwait(false))
            {
                // 完整的补丁文件已经在本地（上次预下载或暂停前下载完的）
                return;
            }
            string path_diff_offset = Path.Join(_context.InstallPath, "ldiff", $"{item.DiffFile.Id}_{item.DiffFile.DiffOffset}");
            string url = item.ManifestUrl.BuildUrl(item.DiffFile.Id);
            await _polly.ExecuteAsync(async t => await _downloadService.DownloadRangeToFileAsync(path_diff_offset, url, item.DiffFile.DiffOffset, item.DiffFile.DiffLength, _context, t).ConfigureAwait(false), token).ConfigureAwait(false);
            lock (_patchDiffOffsetFilesLock)
            {
                _patchDiffOffsetFiles[item] = path_diff_offset;
            }
        }).ConfigureAwait(false);
    }


    /// <summary>
    /// 合并一个 ldiff 补丁。合并结果按清单的大小和 MD5 校验通过后才替换游戏文件，失败时删掉临时文件，游戏文件保持原样。
    /// 原文件与目标不是同一个文件时不在这里删除：同一个原文件可能还要给其他文件合并，等全部合并完后统一删除。
    /// </summary>
    /// <param name="item">补丁任务</param>
    /// <param name="cancellation"></param>
    /// <returns>合并并校验通过（或本次无需更新）为 <see langword="true"/>；原文件缺失或大小不符、合并失败、校验不过为 <see langword="false"/>，需要改为按块重新下载</returns>
    private async Task<bool> TryPatchFileAsync(SophonPatchItem item, CancellationToken cancellation)
    {
        string path_target = Path.Join(_context.InstallPath, item.PatchFile.File);
        long networkSize = item.DiffFile.DiffLength, storageSize = item.PatchFile.Size;
        string md5 = item.PatchFile.Md5;

        string? path_source = null;
        if (!string.IsNullOrWhiteSpace(item.DiffFile.OriginalFileName))
        {
            path_source = Path.Join(_context.InstallPath, item.DiffFile.OriginalFileName);
            FileInfo source = new(path_source);
            if (!source.Exists)
            {
                _logger.LogWarning("Source file not found {file}", path_source);
                return false;
            }
            // 大小对不上必然合并不出正确结果，不必再读一遍文件
            if (item.DiffFile.OriginalFileSize > 0 && source.Length != item.DiffFile.OriginalFileSize)
            {
                _logger.LogWarning("Source file size not match, expected {expected}, actual {actual}. File: {file}", item.DiffFile.OriginalFileSize, source.Length, path_source);
                return false;
            }
        }

        string path_diff = Path.Join(_context.InstallPath, "ldiff", item.DiffFile.Id);
        string? path_diff_offset = null;
        long diffDownloadedBytes = 0;
        if (!await HashService.CheckFileMD5Async(path_diff, item.DiffFile.DiffFileSize, item.DiffFile.DiffFileMd5, cancellation).ConfigureAwait(false))
        {
            lock (_patchDiffOffsetFilesLock)
            {
                _patchDiffOffsetFiles.TryGetValue(item, out path_diff_offset);
            }
            if (path_diff_offset is null || !File.Exists(path_diff_offset))
            {
                // 下载阶段漏掉的（例如上一轮失败后重试），这里补下
                path_diff_offset = Path.Join(_context.InstallPath, "ldiff", $"{item.DiffFile.Id}_{item.DiffFile.DiffOffset}");
                string url = item.ManifestUrl.BuildUrl(item.DiffFile.Id);
                await _polly.ExecuteAsync(async token => await _downloadService.DownloadRangeToFileAsync(path_diff_offset, url, item.DiffFile.DiffOffset, item.DiffFile.DiffLength, _context, token).ConfigureAwait(false), cancellation).ConfigureAwait(false);
            }
            diffDownloadedBytes = new FileInfo(path_diff_offset).Length;
        }

        CreateDirectoryForFile(path_target);
        string path_target_temp = path_target + ".tmp";
        try
        {
            // using 的作用域都在 try 块内：离开后源文件、补丁文件的句柄已关闭，下面替换文件时不会被占用
            using Stream fs_diff = path_diff_offset is null ? new FileSliceStream(path_diff, item.DiffFile.DiffOffset, item.DiffFile.DiffLength) : File.OpenRead(path_diff_offset);
            using var fs_target_temp = File.Create(path_target_temp);
            if (path_source is null)
            {
                if (item.PatchMode is 0 or 1)
                {
                    await fs_diff.CopyToAsync(fs_target_temp, cancellation).ConfigureAwait(false);
                }
                else
                {
                    await HPatch.HPatch.PatchAsync(null, fs_diff, fs_target_temp, cancellationToken: cancellation);
                }
            }
            else
            {
                using FileStream fs_source = File.OpenRead(path_source);
                await HPatch.HPatch.PatchAsync(fs_source, fs_diff, fs_target_temp, cancellationToken: cancellation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch file failed, path: {path}.", path_target);
            if (File.Exists(path_target_temp))
            {
                File.Delete(path_target_temp);
            }
            return false;
        }

        // 只校验结果：原文件损坏同样会让结果对不上，不必另外读一遍原文件
        if (!await HashService.CheckFileMD5Async(path_target_temp, item.PatchFile.Size, item.PatchFile.Md5, cancellation).ConfigureAwait(false))
        {
            File.Delete(path_target_temp);
            _logger.LogWarning("Patched file MD5 not match. File: {path}", path_target);
            return false;
        }
        // 校验通过才替换游戏文件
        File.Move(path_target_temp, path_target, true);
        HardLinkService.InsertGameFileItem(_context.GameId, path_target, storageSize, md5);
        AddPatchOriginalFile(item, path_target);
        // 减去已由 DownloadRangeToFileAsync 完成的部分
        _context.AddNetworkBytes(networkSize - diffDownloadedBytes);
        _context.AddStorageBytes(storageSize - diffDownloadedBytes);
        item.NetworkSize = networkSize;
        item.StorageSize = storageSize;
        item.IsFinished = true;
        return true;
    }



    /// <summary>
    /// ldiff 合并失败的文件改为按块重新下载，完成后把这些文件标记为已完成，继续任务时不再处理。
    /// </summary>
    /// <param name="failedItems">合并失败的文件，此时游戏里仍是旧文件</param>
    /// <param name="cancellation"></param>
    /// <exception cref="InvalidOperationException">目标版本的 Chunk 清单里缺少这些文件。任务会出错，
    /// 但游戏文件只有校验过的才被替换、本地版本号也还没改，修复游戏即可补齐</exception>
    private async Task RedownloadFailedPatchFilesAsync(List<SophonPatchItem> failedItems, CancellationToken cancellation)
    {
        HashSet<string> failedFiles = failedItems.Select(x => x.PatchFile.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SophonChunkItem> chunkItems = await _gameResourceService.GetSophonChunkItemsAsync(_context.GameId, _branch.Main, _context.InstallPath, _context.CategoryScenario, _context.AudioLanguage, null, cancellation).ConfigureAwait(false);
        Dictionary<string, SophonChunkItem> chunkFileDict = new(StringComparer.OrdinalIgnoreCase);
        foreach (SophonChunkItem item in chunkItems)
        {
            chunkFileDict.TryAdd(item.ChunkFile.File, item);
        }
        List<SophonChunkItem> fallbackItems = new();
        List<string> missingFiles = new();
        foreach (string file in failedFiles)
        {
            if (chunkFileDict.TryGetValue(file, out SophonChunkItem? item))
            {
                fallbackItems.Add(item);
            }
            else
            {
                missingFiles.Add(file);
            }
        }
        if (missingFiles.Count > 0)
        {
            _logger.LogError("Files not found in sophon chunk build ({GameBiz}): {Files}", _context.GameId.GameBiz, string.Join(", ", missingFiles));
            throw new InvalidOperationException($"{missingFiles.Count} files that failed to patch are not found in sophon chunk build of ({_context.GameId.GameBiz}).");
        }

        // 这些文件原来按补丁计入进度，改为按块下载后把完整的文件大小补进总量
        long totalNetworkSize = fallbackItems.Sum(x => x.ChunkFile.Chunks.Sum(long (SophonChunk y) => y.CompressedSize));
        long totalStorageSize = fallbackItems.Sum(x => x.ChunkFile.Size);
        _context.NetworkTotalBytes += totalNetworkSize;
        _context.StorageTotalBytes += totalStorageSize;
        _logger.LogInformation("Download {count} files failed to patch in mode chunk, network: {networkBytes} bytes, storage: {storageBytes} bytes.", fallbackItems.Count, totalNetworkSize, totalStorageSize);
        using ChunkDownloadScheduler scheduler = new();
        await _polly.ForEachAsync(fallbackItems, cancellation, async (item, token) =>
        {
            await DownloadSophonChunkFileAsync(item, scheduler, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }



    /// <summary>
    /// 记下补丁用过的原文件，等全部合并完后统一删除（同一个原文件可能还要给其他文件合并）。
    /// 原文件和新文件同路径时不能删
    /// </summary>
    private void AddPatchOriginalFile(SophonPatchItem item, string path_target)
    {
        if (string.IsNullOrWhiteSpace(item.DiffFile.OriginalFileName))
        {
            return;
        }
        string path_original = Path.GetFullPath(Path.Join(_context.InstallPath, item.DiffFile.OriginalFileName));
        if (!string.Equals(path_original, Path.GetFullPath(path_target), StringComparison.OrdinalIgnoreCase))
        {
            lock (_patchOriginalFilesLock)
            {
                _patchOriginalFiles.Add(path_original);
            }
        }
    }



    /// <summary>
    /// 删除 ldiff 合并用过、新版本不再使用的原文件（原文件与目标不是同一个文件时）。
    /// 必须在全部文件处理完后调用，因为同一个原文件可能要给多个文件合并；按全部已完成的文件统计，暂停或出错后继续也能删干净
    /// </summary>
    private void DeletePatchOriginalFiles()
    {
        if (_patchOriginalFiles.Count == 0)
        {
            return;
        }
        int count = 0;
        foreach (string path in _patchOriginalFiles)
        {
            // 新版本的文件不能删：原文件路径可能正好是另一个文件的目标
            if (_patchNewFiles?.Contains(path) is true)
            {
                continue;
            }
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                HardLinkService.DeleteGameFileItem(path);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete original file of patch: {path}", path);
            }
        }
        if (count > 0)
        {
            _logger.LogInformation("Delete {count} original files of patch no longer used in new version.", count);
        }
    }



    private async Task ChunkUpdateAsync(CancellationToken cancellation = default)
    {
        if (_chunkItems is null)
        {
            return;
        }

        _context.ClearProgress();
        _context.Step = RunnerStep.Downloading;
        _context.ProgressDisplay = ProgressDisplay.NetworkSpeed | ProgressDisplay.NetworkBytes | ProgressDisplay.StorageBytes;

        long totalNetworkSize = 0, totalStorageSize = 0;
        foreach (SophonChunkItem item in _chunkItems)
        {
            totalStorageSize += item.ChunkFile.Size;
            foreach (SophonChunk? chunk in item.ChunkFile.Chunks)
            {
                if (!CanReuseOldChunk(chunk))
                {
                    totalNetworkSize += chunk.CompressedSize;
                }
            }
        }
        _context.NetworkTotalBytes = totalNetworkSize;
        _context.StorageTotalBytes = totalStorageSize;
        _logger.LogInformation("Start chunk update download, file count: {count}, network: {networkBytes} bytes, storage: {storageBytes} bytes.", _chunkItems.Count, totalNetworkSize, totalStorageSize);

        // 块下载并行、按序写入，共用一个限流器限制同时下载的块数
        using ChunkDownloadScheduler scheduler = new();
        // 任务包含新版本全部文件，没变的文件也要先完整读一遍校验，变了的还要从旧文件按块读取，瓶颈在磁盘
        ParallelOptions options = new() { MaxDegreeOfParallelism = GetLocalFileParallelism(ChunkFileParallelismOnHdd), CancellationToken = cancellation };
        await _polly.ForEachAsync(_chunkItems, options, async (item, token) =>
        {
            if (AddBytesIfFinished(item))
            {
                return;
            }

            string fullPath = Path.Join(_context.InstallPath, item.ChunkFile.File);
            long networkSize = 0, storageSize = item.ChunkFile.Size;
            foreach (var chunk in item.ChunkFile.Chunks)
            {
                if (!CanReuseOldChunk(chunk))
                {
                    networkSize += chunk.CompressedSize;
                }
            }
            string md5 = item.ChunkFile.Md5;

            if (await TryHardLinkAsync(item, fullPath, networkSize, storageSize, md5, token).ConfigureAwait(false))
            {
                return;
            }

            if (await CheckFileMD5Async(item, fullPath, networkSize, storageSize, md5, true, token).ConfigureAwait(false))
            {
                HardLinkService.InsertGameFileItem(_context.GameId, fullPath, storageSize, md5);
                return;
            }

            await DownloadSophonChunkFileContentAsync(item, scheduler, _oldChunkDict, networkSize, storageSize, fullPath, false, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

    }



    /// <summary>
    /// 新版本的块能否从旧版本文件里按内容复用。只看大小是否对得上，实际复制前还会校验截取出的内容
    /// </summary>
    private bool CanReuseOldChunk(SophonChunk chunk)
    {
        return _oldChunkDict?.TryGetValue(chunk.UncompressedMd5, out var old) is true
            && old.Chunk.UncompressedSize == chunk.UncompressedSize;
    }



    protected override async Task PostAsync(CancellationToken cancellation = default)
    {
        _context.ClearProgress();
        _context.Step = RunnerStep.Cleaning;
        _context.ProgressDisplay = ProgressDisplay.None;
        try
        {
            DeletePatchOriginalFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete original files of patch.");
        }
        try
        {
            if (_deleteFiles is { Count: > 0 })
            {
                _logger.LogInformation("Delete {count} outdated files.", _deleteFiles.Count);
                int count = DeleteRemovedFiles(_deleteFiles);
                _logger.LogInformation("Deleted {count} of {total} outdated files.", count, _deleteFiles.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete outdated files.");
        }
        try
        {
            string chunkFolder = Path.Join(_context.InstallPath, "chunk");
            if (Directory.Exists(chunkFolder))
            {
                Directory.Delete(chunkFolder, true);
                HardLinkService.DeleteGameFolder(chunkFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete chunk folder.");
        }
        try
        {
            string ldiffFolder = Path.Join(_context.InstallPath, "ldiff");
            if (Directory.Exists(ldiffFolder))
            {
                Directory.Delete(ldiffFolder, true);
                HardLinkService.DeleteGameFolder(ldiffFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete ldiff folder.");
        }
        _context.ConfigIni.Settings.Remove("predownload");
    }


}
