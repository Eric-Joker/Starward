using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameSelector;
using Starward.Features.GameSetting;
using Starward.Features.Passport;
using Starward.Features.PlayTime;
using Starward.GameInstall;
using Starward.GameInstall.HotUpdate;
using Starward.GameInstall.Model;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameLauncher;

internal partial class GameLauncherService
{
    private readonly ILogger<GameLauncherService> _logger;

    private readonly HoYoPlayService _hoYoPlayService;

    private readonly GameConfigService _gameConfigService;

    private readonly PassportService _passportService;

    private readonly PlayTimeRecordService _playTimeRecorderService;

    private readonly GenshinHotUpdateService _genshinHotUpdateService;


    /// <summary>
    /// 同一个游戏的全部区服，用来找出需要与游戏进程所在目录区分的其他区服。
    /// </summary>
    private static readonly string[] GameServers = ["cn", "bilibili", "global"];


    public GameLauncherService(ILogger<GameLauncherService> logger,
                               HoYoPlayService hoYoPlayService,
                               PassportService passportService,
                               GameConfigService gameConfigService,
                               PlayTimeRecordService playTimeRecorderService,
                               GenshinHotUpdateService genshinHotUpdateService)
    {
        _logger = logger;
        _hoYoPlayService = hoYoPlayService;
        _gameConfigService = gameConfigService;
        _passportService = passportService;
        _playTimeRecorderService = playTimeRecorderService;
        _genshinHotUpdateService = genshinHotUpdateService;
    }


    #region Install Path


    /// <summary>
    /// 游戏安装目录，为空时未找到
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public static string? GetGameInstallPath(GameId gameId)
    {
        GameEntry? entry = GameEntryService.GetFromId(gameId);
        if (string.IsNullOrWhiteSpace(entry?.InstallPath))
        {
            return null;
        }
        string path = GetFullPathIfRelativePath(entry.InstallPath);
        if (Directory.Exists(path))
        {
            return Path.GetFullPath(path);
        }
        else if (entry.Removable)
        {
            return path;
        }
        else
        {
            GameEntryService.SetInstallPathNull(gameId);
            return null;
        }
    }


    /// <summary>
    /// 游戏安装目录，为空时未找到
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="storageRemoved">可移动存储设备已移除</param>
    /// <returns></returns>
    public static string? GetGameInstallPath(GameId gameId, out bool storageRemoved)
    {
        storageRemoved = false;
        GameEntry? entry = GameEntryService.GetFromId(gameId);
        if (string.IsNullOrWhiteSpace(entry?.InstallPath))
        {
            return null;
        }
        string path = GetFullPathIfRelativePath(entry.InstallPath);
        if (Directory.Exists(path))
        {
            return path;
        }
        else if (entry.Removable)
        {
            storageRemoved = true;
            return path;
        }
        else
        {
            GameEntryService.SetInstallPathNull(gameId);
            return null;
        }
    }

    /// <summary>
    /// 修改游戏安装目录
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    public string? ChangeGameInstallPath(GameId gameId, string? path)
    {
        LauncherConfig? model = _hoYoPlayService.GetLauncherConfig(gameId);
        if (model is null)
        {
            throw new ArgumentNullException($"Cannot get LauncherConfig from GameId ({gameId}).");
        }
        if (Directory.Exists(path))
        {
            path = Path.GetFullPath(path);
            string relativePath = GetRelativePathIfInRemovableStorage(path, out bool removable);
            GameEntryService.SetInstallPath(gameId, model.Id, relativePath, removable);
            WeakReferenceMessenger.Default.Send(new PinGameIconMessage(gameId, model, true));
        }
        else
        {
            path = null;
            GameEntryService.SetInstallPath(gameId, model.Id, null, false);
        }
        return path;
    }


    /// <summary>
    /// 如果安装在可移动存储设备中，获取相对路径
    /// </summary>
    /// <param name="path"></param>
    /// <param name="removableStorage"></param>
    /// <returns></returns>
    public static string GetRelativePathIfInRemovableStorage(string path, out bool removableStorage)
    {
        removableStorage = DriveHelper.IsDeviceRemovableOrOnUSB(path);
        if (removableStorage && Path.GetPathRoot(AppConfig.StarwardExecutePath) == Path.GetPathRoot(path))
        {
            path = Path.GetRelativePath(Path.GetDirectoryName(AppConfig.ConfigPath)!, path);
        }
        return path;
    }


    /// <summary>
    /// 如果安装在可移动存储设备中，获取完整路径
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static string GetFullPathIfRelativePath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }
        else
        {
            return Path.GetFullPath(path, Path.GetDirectoryName(AppConfig.ConfigPath)!);
        }
    }


    #endregion



    #region Game Version


    /// <summary>
    /// 本地游戏版本
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="installPath"></param>
    /// <returns></returns>
    public async Task<Version?> GetLocalGameVersionAsync(GameId gameId, string? installPath = null, CancellationToken cancellation = default)
    {
        installPath ??= GetGameInstallPath(gameId);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }
        return (await GameConfigService.GetGameConfigIniAsync(installPath, cancellation))?.GameVersion;
    }


    /// <summary>
    /// 最新游戏版本
    /// </summary>
    /// <param name="gameBiz"></param>
    /// <returns></returns>
    public async Task<(Version? Latest, Version? Predownload)> GetLatestGameVersionAsync(GameId gameId, CancellationToken cancellation)
    {
        LauncherConfig? launcher = _hoYoPlayService.GetLauncherConfig(gameId);
        GameConfig? config = await _hoYoPlayService.GetGameConfigAsync(gameId, cancellation);
        if (config is null)
        {
            throw new ArgumentOutOfRangeException($"Game config is null ({gameId.Id}, {gameId.GameBiz}).");
        }
        if (config.DefaultDownloadMode is DownloadMode.DOWNLOAD_MODE_FILE)
        {
            GamePackage? package = await _hoYoPlayService.GetGamePackageAsync(gameId, cancellation);
            _ = Version.TryParse(package?.Main.Major?.Version, out Version? latestVersion);
            _ = Version.TryParse(package?.PreDownload.Major?.Version, out Version? predownloadVersion);
            return (latestVersion, predownloadVersion);
        }
        else
        {
            GameBranch? gameBranch = await _hoYoPlayService.GetGameBranchAsync(gameId, cancellation);
            if (gameBranch is null)
            {
                throw new ArgumentOutOfRangeException($"Game branch is null ({gameId.Id}, {gameId.GameBiz}).");
            }
            if (launcher?.IsBeta is true)
            {
                await _hoYoPlayService.GetGameSophonChunkBuildAsync(gameId, gameBranch.Main, null, cancellation);
            }
            _ = Version.TryParse(gameBranch.Main.Tag, out Version? latestVersion);
            _ = Version.TryParse(gameBranch.PreDownload?.Tag, out Version? predownloadVersion);
            return (latestVersion, predownloadVersion);
        }
    }


    /// <summary>
    /// 检查预下载是否完成
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="installPath"></param>
    /// <returns></returns>
    public async Task<bool> CheckPreDownloadFinishedAsync(GameId gameId, string? installPath = null, CancellationToken cancellation = default)
    {
        installPath ??= GetGameInstallPath(gameId);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }
        var config = await GameConfigService.GetGameConfigIniAsync(installPath, cancellation);
        if (config is null)
        {
            return false;
        }
        (_, Version? pre) = await GetLatestGameVersionAsync(gameId, cancellation);
        AudioLanguage language = await _gameConfigService.GetAudioLanguageAsync(gameId, installPath, cancellation);
        return config.Settings.GetValueOrDefault("predownload") == $"{config.GameVersion},{pre},{language}";
    }


    /// <summary>
    /// 检查插件更新
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="installPath"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public async Task<bool> CheckPluginUpdateAsync(GameId gameId, string? installPath = null, CancellationToken cancellation = default)
    {
        installPath ??= GetGameInstallPath(gameId);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }
        GamePluginRelease? pluginRelease = await _hoYoPlayService.GetGamePluginAsync(gameId, cancellation);
        if (pluginRelease is null)
        {
            return false;
        }
        GameConfigIni? configIni = await GameConfigService.GetGameConfigIniAsync(installPath, cancellation);
        foreach (var item in pluginRelease.Plugins)
        {
            if (item.PluginPackage.Url.Contains("DXSETUP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string key = $"plugin_{item.PluginId}_version";
            if (configIni?.Settings.GetValueOrDefault(key) == item.Version)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.PluginPackage.Validation))
            {
                return true;
            }
            else if (!await GameResourceService.VerifyPluginAsync(installPath, item.PluginPackage, cancellation))
            {
                return true;
            }
        }
        return false;
    }



    public async Task<bool> CheckGenshinHotUpdateAsync(GameId gameId, string? installPath = null, CancellationToken cancellation = default)
    {
        if (gameId.GameBiz.Game is not GameBiz.hk4e || !AppConfig.GetEnableCutsceneManage(gameId))
        {
            return false;
        }
        var launcher = _hoYoPlayService.GetLauncherConfig(gameId);
        if (launcher is null || launcher.IsBeta)
        {
            return false;
        }
        installPath ??= GetGameInstallPath(gameId);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }
        return await _genshinHotUpdateService.CheckUpdateAsync(gameId, installPath, cancellation);
    }



    #endregion



    #region Start Game


    /// <summary>
    /// 游戏进程名，带 .exe 扩展名
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public async Task<string> GetGameExeNameAsync(GameId gameId, CancellationToken cancellation)
    {
        string? name = GetGameExeName(gameId.GameBiz);
        if (string.IsNullOrWhiteSpace(name))
        {
            var config = await _hoYoPlayService.GetGameConfigAsync(gameId, cancellation);
            name = config?.ExeFileName;
        }
        return name ?? throw new ArgumentOutOfRangeException($"Unknown game ({gameId.Id}, {gameId.GameBiz}).");
    }


    /// <summary>
    /// 游戏进程名，带 .exe 扩展名
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public static string? GetGameExeName(GameBiz gameBiz)
    {
        string? name = gameBiz.Value switch
        {
            GameBiz.hk4e_cn or GameBiz.hk4e_bilibili => "YuanShen.exe",
            GameBiz.hk4e_global => "GenshinImpact.exe",
            _ => gameBiz.Game switch
            {
                GameBiz.hkrpg => "StarRail.exe",
                GameBiz.bh3 => "BH3.exe",
                GameBiz.nap => "ZenlessZoneZero.exe",
                _ => null,
            },
        };
        return name;
    }


    /// <summary>
    /// 游戏进程文件是否存在
    /// </summary>
    /// <param name="biz"></param>
    /// <param name="installPath"></param>
    /// <returns></returns>
    public async Task<bool> IsGameExeExistsAsync(GameId gameId, string? installPath = null, CancellationToken cancellation = default)
    {
        installPath ??= GetGameInstallPath(gameId);
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            var exe = Path.Join(installPath, await GetGameExeNameAsync(gameId, cancellation));
            return File.Exists(exe);
        }
        return false;
    }


    /// <summary>
    /// 获取游戏进程
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public async Task<Process?> GetGameProcessAsync(GameId gameId, CancellationToken cancellation)
    {
        int currentSessionId = Process.GetCurrentProcess().SessionId;
        var name = (await GetGameExeNameAsync(gameId, cancellation)).Replace(".exe", "");
        // 同一个游戏的区服可能用同一个进程名（hk4e 的国服与 B 服都是 YuanShen.exe），只按进程名会把另一个区服在运行
        // 也算成本区服在运行，所以本区服安装目录里的进程优先，在别的区服安装目录里的直接排除；
        // 其余进程（装在别处、记录里没有安装目录）仍按进程名认，否则游戏明明在运行却会被当成没运行
        Process? fallback = null;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.SessionId != currentSessionId || IsProcessPending(process))
                {
                    continue;
                }
                string? imagePath = ProcessHelper.TryGetProcessImagePath(process);
                if (IsProcessInRecordedInstallPath(gameId, imagePath))
                {
                    return process;
                }
                if (IsProcessInOtherServerInstallPath(gameId, imagePath))
                {
                    // 在别的区服的安装目录里，不是本区服在运行
                    _logger.LogInformation("Game process ({gamePid}, {imagePath}) is in the install path of another server, ignore it ({biz})", process.Id, imagePath, gameId.GameBiz);
                    continue;
                }
                fallback ??= process;
            }
            catch { }
        }
        return fallback;
    }



    /// <summary>
    /// 进程是否运行在该区服记录的安装目录里。
    /// </summary>
    /// <param name="gameId">区服。</param>
    /// <param name="imagePath">进程的 exe 完整路径。</param>
    /// <returns>记录的安装目录对不上（含没有记录、取不到进程路径）时返回 <see langword="false"/>。</returns>
    private static bool IsProcessInRecordedInstallPath(GameId gameId, string? imagePath)
    {
        string? directory = imagePath is null ? null : Path.GetDirectoryName(imagePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }
        string? recorded = GetRecordedInstallPath(gameId);
        return recorded is not null && string.Equals(Path.TrimEndingDirectorySeparator(directory), recorded, StringComparison.OrdinalIgnoreCase);
    }



    /// <summary>
    /// 进程是否运行在同一个游戏的其他区服的安装目录里。
    /// </summary>
    /// <param name="gameId">当前区服。</param>
    /// <param name="imagePath">进程的 exe 完整路径。</param>
    /// <returns>在别的区服安装目录里时返回 <see langword="true"/>。</returns>
    private static bool IsProcessInOtherServerInstallPath(GameId gameId, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }
        foreach (string server in GameServers)
        {
            string biz = $"{gameId.GameBiz.Game}_{server}";
            if (biz != gameId.GameBiz && GameId.FromGameBiz(biz) is GameId otherGameId && IsProcessInRecordedInstallPath(otherGameId, imagePath))
            {
                return true;
            }
        }
        return false;
    }



    /// <summary>
    /// 区服记录的安装目录，已展开成完整路径并去掉结尾的目录分隔符。
    /// </summary>
    /// <param name="gameId">区服。</param>
    /// <returns>没有记录时返回 <see langword="null"/>。目录不存在时不清掉记录，只按记录比较。</returns>
    private static string? GetRecordedInstallPath(GameId gameId)
    {
        try
        {
            string? path = GetGameInstallPath(gameId);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return Path.TrimEndingDirectorySeparator(GetFullPathIfRelativePath(path));
        }
        catch
        {
            return null;
        }
    }


    /// <summary>
    /// 检测进程挂起
    /// </summary>
    /// <param name="process"></param>
    /// <returns></returns>
    public static bool IsProcessPending(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }
            foreach (ProcessThread thread in process.Threads)
            {
                if (thread.ThreadState is not System.Diagnostics.ThreadState.Wait)
                {
                    return false;
                }
                else if (thread.WaitReason is not ThreadWaitReason.Suspended)
                {
                    return false;
                }
            }
            return true;
        }
        catch { }
        return true;
    }


    /// <summary>
    /// 启动游戏
    /// </summary>
    /// <returns></returns>
    public async Task<Process?> StartGameAsync(GameId gameId, string? installPath = null, CancellationToken cancellation = default)
    {
        const int ERROR_CANCELLED = 0x000004C7;
        try
        {
            if (await GetGameProcessAsync(gameId, cancellation) is Process existingProcess)
            {
                throw new Exception($"Game is running: {existingProcess.ProcessName}.exe ({existingProcess.Id}).");
            }
            string? exe = null, verb = null;
            if (Directory.Exists(installPath))
            {
                var e = Path.Join(installPath, await GetGameExeNameAsync(gameId, cancellation));
                if (File.Exists(e))
                {
                    exe = e;
                }
            }
            bool thirdPartyTool = false;
            if (string.IsNullOrWhiteSpace(exe) && AppConfig.GetEnableThirdPartyTool(gameId))
            {
                exe = GetThirdPartyToolPath(gameId);
                if (File.Exists(exe))
                {
                    thirdPartyTool = true;
                    verb = Path.GetExtension(exe) is ".exe" or ".bat" ? "runas" : "";
                }
                else
                {
                    exe = null;
                    SetThirdPartyToolPath(gameId, null);
                    _logger.LogWarning("Third party tool not found: {path}", exe);
                }
            }
            if (string.IsNullOrWhiteSpace(exe))
            {
                var folder = GetGameInstallPath(gameId);
                var name = await GetGameExeNameAsync(gameId, cancellation);
                exe = Path.Join(folder, name);
                verb = "runas";
                if (!File.Exists(exe))
                {
                    _logger.LogWarning("Game exe not found: {path}", exe);
                    throw new FileNotFoundException("Game exe not found", name);
                }
            }

            CancellationToken token = CancellationTokenSource.CreateLinkedTokenSource(cancellation, new CancellationTokenSource(3000).Token).Token;
            var sb = new StringBuilder();
            sb.Append(AppConfig.GetStartArgument(gameId)?.Trim());
            await AppendDX12Async(sb, gameId, token);
            await AppendLoginAuthTicketAsync(sb, gameId, token);
            SetGenshinEnableHDR(gameId);
            string arg = sb.ToString();

            if (!thirdPartyTool && AppConfig.StartGameWithCMD)
            {
                arg = $"""/c start "" /d "{Path.GetDirectoryName(exe)}" "{exe}" {arg}""";
                exe = "cmd.exe";
            }
            var info = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arg,
                UseShellExecute = true,
                Verb = verb,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            Process? process = Process.Start(info);
            if (process != null)
            {
                if (thirdPartyTool || AppConfig.StartGameWithCMD)
                {
                    // 能跟随启动程序时把它的进程和 exe 路径一并交给记时逻辑：工具还活着就一直等它拉起游戏
                    Process? launcherProcess = null;
                    string? launcherImagePath = null;
                    if (thirdPartyTool && IsThirdPartyToolFollowable(exe))
                    {
                        launcherProcess = process;
                        launcherImagePath = GetThirdPartyToolImagePath(exe, process);
                    }
                    return await _playTimeRecorderService.StartProcessToLogAsync(gameId, launcherProcess, launcherImagePath);
                }
                else
                {
                    await _playTimeRecorderService.StartProcessToLogAsync(gameId, process.Id);
                    return process;
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            // Operation canceled
            _logger.LogInformation("Start game operation canceled.");
        }
        return null;
    }


    private async Task AppendDX12Async(StringBuilder sb, GameId gameId, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        try
        {
            var gameConfig = await _hoYoPlayService.GetGameConfigAsync(gameId, cancellation);
            if (gameConfig?.EnableDxSwitch is true)
            {
                var dxConfig = await _hoYoPlayService.GetDXConfigAsync(gameId, GPUInfoHelper.GetGPUInfos(), cancellation);
                if (dxConfig?.EnableDXSwitch is true)
                {
                    sb.Append(' ');
                    bool? enable = AppConfig.GetEnableDX12(gameId);
                    if (dxConfig.AutoMode && enable.HasValue)
                    {
                        sb.Append(enable.Value ? dxConfig.CmdArgs : dxConfig.DX11CmdArgs);
                    }
                    else if (enable.HasValue)
                    {
                        sb.Append(enable.Value ? dxConfig.CmdArgs : dxConfig.DX11CmdArgs);
                    }
                    else
                    {
                        sb.Append(dxConfig.UseDX12ByDefault ? dxConfig.CmdArgs : dxConfig.DX11CmdArgs);
                    }
                }
            }
        }
        catch { }
    }




    /// <summary>
    /// 自定义启动程序是否能按进程跟随。
    /// </summary>
    /// <param name="exe">启动时打开的文件。</param>
    /// <returns>
    /// 可执行文件与批处理返回 <see langword="true"/>（批处理的中转 cmd.exe 只跟随进程）；
    /// <c>.url</c>、<c>.lnk</c> 等由系统转交给浏览器或资源管理器的返回 <see langword="false"/>，跟随它们会一直等下去。
    /// </returns>
    private static bool IsThirdPartyToolFollowable(string? exe)
    {
        string? extension = Path.GetExtension(exe);
        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }




    /// <summary>
    /// 取刚启动的自定义启动程序的 exe 完整路径。单实例程序（如常驻托盘的胡桃工具箱）已在运行时，
    /// 新实例把请求转交给已有实例后马上退出，之后要按此路径找到已有实例继续等它拉起游戏。
    /// </summary>
    /// <param name="exe">启动时打开的文件。</param>
    /// <param name="process">打开后拿到的进程，可为 <see langword="null"/>。</param>
    /// <returns>exe 完整路径；拿不到，或拿到的不是启动程序本身时返回 <see langword="null"/>。</returns>
    private static string? GetThirdPartyToolImagePath(string? exe, Process? process)
    {
        try
        {
            string? path = null;
            if (!string.IsNullOrWhiteSpace(exe) && string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                // 进程一退出就读不到路径（实测 QueryFullProcessImageName 返回错误 31），只能趁刚启动时读
                path = process is null ? null : ProcessHelper.TryGetProcessImagePath(process);
                path ??= Path.GetFullPath(exe);
            }
            // Windows 目录下的程序（如批处理转交的 cmd.exe）不代表启动程序本身，随时可能有无关实例在运行，按路径跟随会一直等下去
            string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
            if (path is not null && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return path;
        }
        catch
        {
            return null;
        }
    }



    private async Task AppendLoginAuthTicketAsync(StringBuilder sb, GameId gameId, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (AppConfig.AuthLoginGameByPassportAccount)
            {
                string? ticket = await _passportService.CreateAuthTicketAsync(gameId, cancellation);
                if (!string.IsNullOrWhiteSpace(ticket))
                {
                    sb.Append($" login_auth_ticket={ticket}");
                    if (AppConfig.IsAppInRemovableStorage)
                    {
                        sb.Append(" login_storage_type=session");
                    }
                    else
                    {
                        sb.Append(" login_storage_type=local");
                    }
                }
            }
        }
        catch { }
    }


    private void SetGenshinEnableHDR(GameId gameId)
    {
        if (gameId.GameBiz.Game is GameBiz.hk4e)
        {
            GameSettingService.SetGenshinEnableHDR(gameId.GameBiz, AppConfig.EnableGenshinHDR);
        }
    }


    #endregion



    #region Third Party Tool


    /// <summary>
    /// 获取第三方工具路径
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public static string? GetThirdPartyToolPath(GameId gameId)
    {
        string? path = AppConfig.GetThirdPartyToolPath(gameId);
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = GetFullPathIfRelativePath(path);
        }
        if (File.Exists(path))
        {
            return path;
        }
        else
        {
            AppConfig.SetThirdPartyToolPath(gameId, null);
            return null;
        }
    }


    /// <summary>
    /// 设置第三方工具路径
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    public static string? SetThirdPartyToolPath(GameId gameId, string? path)
    {
        if (File.Exists(path))
        {
            path = Path.GetFullPath(path);
            string relativePath = GetRelativePathIfInRemovableStorage(path, out bool removable);
            AppConfig.SetThirdPartyToolPath(gameId, relativePath);
        }
        else
        {
            path = null;
            AppConfig.SetThirdPartyToolPath(gameId, null);
        }
        return path;
    }


    #endregion


}
