using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Starward.Core.HoYoPlay;
using Starward.GameInstall.Model;
using Starward.GameInstall.Service;
using System.Text.Json;

namespace Starward.GameInstall.Step;

internal class StepBase
{

    private bool _completed;

    protected readonly GameInstallContext _context;

    protected readonly HoYoPlayService _hoYoPlayService;

    protected readonly DownloadService _downloadService;

    protected readonly GameConfigService _gameConfigService;

    protected readonly GameResourceService _gameResourceService;

    protected readonly ILogger _stepLogger;

    protected readonly ResiliencePipeline _polly;


    public StepBase(GameInstallContext context, IServiceProvider serviceProvider)
    {
        _context = context;
        _hoYoPlayService = serviceProvider.GetRequiredService<HoYoPlayService>();
        _downloadService = serviceProvider.GetRequiredService<DownloadService>();
        _gameConfigService = serviceProvider.GetRequiredService<GameConfigService>();
        _gameResourceService = serviceProvider.GetRequiredService<GameResourceService>();
        _stepLogger = serviceProvider.GetRequiredService<ILogger<StepBase>>();
        _polly = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Linear,
            OnRetry = args =>
            {
                _stepLogger.LogWarning(args.Outcome.Exception, "Operation failed, retry attempt {attempt}.", args.AttemptNumber + 1);
                return default;
            }
        }).Build();
    }


    public bool SkipStep { get; protected set; }



    public async Task ExecuteAsync(CancellationToken cancellation = default)
    {
        if (_completed)
        {
            return;
        }
        string step = GetType().Name;
        using IDisposable? scope = _stepLogger.BeginScope(new Dictionary<string, object?> { ["ExtraContext"] = $"[GameBiz={_context.GameId.GameBiz}, Operation={_context.Operation}, Step={step}]" });
        _stepLogger.LogInformation("Step started.");
        try
        {
            await ExecuteInternalAsync(cancellation).ConfigureAwait(false);
            await PostAsync(cancellation).ConfigureAwait(false);
            _completed = true;
            _stepLogger.LogInformation("Step finished.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stepLogger.LogError(ex, "Step failed.");
            throw;
        }
    }


    public virtual Task PrepareAsync(CancellationToken cancellation = default)
    {
        return Task.CompletedTask;
    }


    protected virtual Task ExecuteInternalAsync(CancellationToken cancellation = default)
    {
        return Task.CompletedTask;
    }


    protected virtual Task PostAsync(CancellationToken cancellation = default)
    {
        return Task.CompletedTask;
    }



    protected static void CreateDirectoryForFile(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }
    }


    protected virtual async Task MoveCachedResourceAsync(CancellationToken cancellation = default)
    {
        GameConfig config = _context.GameConfig;

        if (!string.IsNullOrWhiteSpace(config.AudioPackageResDir) && !string.IsNullOrWhiteSpace(config.AudioPackageCacheDir))
        {
            cancellation.ThrowIfCancellationRequested();
            string cacheDir = Path.Join(_context.InstallPath, config.AudioPackageCacheDir);
            if (Directory.Exists(cacheDir))
            {
                string resDir = Path.Join(_context.InstallPath, config.AudioPackageResDir);
                int count = MoveAllFiles(cacheDir, resDir, cancellation);
                if (count > 0)
                {
                    _stepLogger.LogInformation("Moved {count} audio package cache files from {cacheDir} to {resDir}.", count, cacheDir, resDir);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(config.LocalResDir) && !string.IsNullOrWhiteSpace(config.LocalResCacheDir) && !string.IsNullOrWhiteSpace(config.GameResCutDir))
        {
            cancellation.ThrowIfCancellationRequested();
            string resCut = Path.Join(_context.InstallPath, config.GameResCutDir);
            if (File.Exists(resCut))
            {
                int count = 0;
                using var fs = File.OpenRead(resCut);
                var items = JsonSerializer.DeserializeAsyncEnumerable<BlacklistItem>(fs, true, cancellationToken: cancellation);
                await foreach (BlacklistItem? item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item?.FileName))
                    {
                        string target = Path.Join(_context.InstallPath, item.FileName);
                        string source = Path.Join(_context.InstallPath, item.FileName.Replace(config.LocalResDir, config.LocalResCacheDir));
                        if (File.Exists(source) && !File.Exists(target))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Move(source, target, true);
                            count++;
                        }
                    }
                }
                if (count > 0)
                {
                    _stepLogger.LogInformation("Moved {count} local resource cache files from {cacheDir} to {resDir}.", count, config.LocalResCacheDir, config.LocalResDir);
                }
            }
        }
    }


    private static int MoveAllFiles(string sourceDir, string targetDir, CancellationToken cancellation = default)
    {
        int count = 0;
        foreach (string source in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellation.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceDir, source);
            string target = Path.GetFullPath(Path.Combine(targetDir, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, true);
            count++;
        }
        return count;
    }



    /// <summary>
    /// 固态硬盘上同时处理（校验、重建、合并）的文件数上限。2026-10 实测 NVMe 校验 12~16 路最快（约 3.2 GiB/s），
    /// 24~32 路反而略慢，边读边写 8 路左右就到顶，所以 CPU 核数再多也不超过这个数
    /// </summary>
    private const int MaxFileParallelismOnSsd = 16;

    /// <summary>
    /// 非固态硬盘上 chunk 模式（修复、更新）同时处理的文件数。官方启动器 chunk 校验下发 6 线程，
    /// 但 2026-10 实测机械硬盘 1 路最快，6 路边读边写比 1 路慢约 40%；留 2 路，让块少的小文件处理时也有块在下载
    /// </summary>
    protected const int ChunkFileParallelismOnHdd = 2;

    /// <summary>
    /// 官方启动器 ldiff 补丁的并发数（getParamsConfig 按 disk_type 下发，2026-09 实测 SSD 以外为 2）。
    /// 补丁下载不跟随 CPU 核数，也不区分磁盘类型，固定用这个值；ldiff 合并在机械硬盘上也用它限制文件数
    /// </summary>
    protected const int OfficialLdiffParallelism = 2;

    /// <summary>
    /// 官方启动器整包 / 单文件模式的校验线程数（getParamsConfig 的 max_validation_threads，2026-09 实测机械硬盘与未识别的磁盘为 1；
    /// 修复的瓶颈是磁盘，非固态硬盘沿用 1 路）
    /// </summary>
    private const int OfficialFileVerifyThreads = 1;



    /// <summary>
    /// 大量读写本地文件的阶段（chunk 更新、ldiff 合并）同时处理的文件数：游戏在固态硬盘上按 CPU 核数、
    /// 最多 <see cref="MaxFileParallelismOnSsd"/>，否则用传入的固定值，避免机械硬盘被多路并发读写拖慢
    /// </summary>
    /// <param name="parallelismOnHdd">不在固态硬盘上时同时处理的文件数</param>
    protected int GetLocalFileParallelism(int parallelismOnHdd)
    {
        bool ssd = DriveHelper.IsSolidStateDrive(_context.InstallPath);
        int parallelism = ssd ? Math.Min(Environment.ProcessorCount, MaxFileParallelismOnSsd) : parallelismOnHdd;
        _stepLogger.LogInformation("Local file parallelism: {parallelism}, solid state drive: {ssd}, operation: {operation}", parallelism, ssd, _context.Operation);
        return parallelism;
    }



    /// <summary>
    /// 修复时同时校验 / 重新下载的文件数：游戏在固态硬盘上按 CPU 核数、最多 <see cref="MaxFileParallelismOnSsd"/>，
    /// 否则用较小的固定值，避免机械硬盘、可移动存储和网络位置被多路并发读取拖慢
    /// </summary>
    /// <param name="chunkMode">是否为 chunk 模式；整包 / 单文件模式按整包的校验线程数</param>
    protected int GetRepairParallelism(bool chunkMode)
    {
        bool ssd = DriveHelper.IsSolidStateDrive(_context.InstallPath);
        int threads = ssd ? Math.Min(Environment.ProcessorCount, MaxFileParallelismOnSsd) : chunkMode ? ChunkFileParallelismOnHdd : OfficialFileVerifyThreads;
        _stepLogger.LogInformation("Repair parallelism: {threads}, solid state drive: {ssd}, chunk mode: {chunk}", threads, ssd, chunkMode);
        return threads;
    }

}