using Microsoft.Extensions.Logging;
using Starward.Core;
using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace Starward.GameInstall.Service;


/// <summary>
/// 修复硬链接的多个区服时，把其他区服共用同一份数据的文件重新链接到修好的文件上。
/// 硬链接的区服在磁盘上用的是同一份数据，本区服的文件坏了，对方的也坏了；修复用新文件替换本区服的路径后，
/// 对方仍指向旧数据，所以要在替换前记下这些路径，替换后链接到修好的文件。只用于修复操作。
/// </summary>
internal class SharedInstallLinkService
{

    private readonly ILogger<SharedInstallLinkService> _logger;


    public SharedInstallLinkService(ILogger<SharedInstallLinkService> logger)
    {
        _logger = logger;
    }



    /// <summary>
    /// 列出与本区服这个文件共用同一份数据、需要一并修好的其他区服文件。
    /// 只收录同一游戏、config.ini 版本与修复目标版本相同、对方目录中存在同一相对路径、并且文件在对方目录中的位置与本区服一致的路径
    /// （原神国服与国际服的数据目录名不同也能对上）：版本不同时两边这个文件该有的内容可能不同，不能动。
    /// </summary>
    /// <param name="context">修复任务，其他操作返回空</param>
    /// <param name="path">本区服即将被替换的文件完整路径</param>
    /// <param name="relativePath">该文件相对于游戏安装目录的路径</param>
    /// <returns>其他区服中要重新链接的完整路径，必须在替换文件之前调用</returns>
    public List<string> GetSharedInstallFiles(GameInstallContext context, string path, string relativePath)
    {
        List<string> result = [];
        if (context.Operation is not GameInstallOperation.Repair || string.IsNullOrWhiteSpace(relativePath) || !File.Exists(path))
        {
            return result;
        }
        try
        {
            if (!Kernel32.GetVolumePathName(path, out string? volume) || string.IsNullOrWhiteSpace(volume))
            {
                return result;
            }
            string installPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.InstallPath)) + Path.DirectorySeparatorChar;
            foreach (string name in Kernel32.EnumHardLinks(path))
            {
                // 返回的是不带盘符的卷内路径，如 \Games\hk4e_bilibili\...
                string linkPath = Path.GetFullPath(Path.Join(volume, name.TrimStart(Path.DirectorySeparatorChar)));
                if (linkPath.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // 只重建对方本来就有这个文件的情况：对方没有这个文件时说明两边文件清单本来就不同
                // （语音包、平台资源等），贸然补上会凭空多出文件并破坏对方的文件完整性
                if (!File.Exists(linkPath))
                {
                    continue;
                }
                if (GetSharedInstallRoot(linkPath, relativePath) is string root && IsSharedInstallRepairable(context, root))
                {
                    result.Add(linkPath);
                }
            }
        }
        catch (Exception ex)
        {
            // 只影响其他区服，不能让本区服的修复失败
            _logger.LogWarning(ex, "Failed to enumerate hard links of {file}", path);
        }
        return result;
    }



    /// <summary>
    /// 把 <see cref="GetSharedInstallFiles"/> 记下的其他区服文件重新链接到本区服修好的文件，两边恢复共用同一份数据。
    /// 对方游戏正在运行等原因替换失败时只记日志，不影响本区服的修复。
    /// </summary>
    /// <param name="context">修复任务</param>
    /// <param name="source">本区服修好并通过校验的文件</param>
    /// <param name="paths">其他区服中要重新链接的完整路径</param>
    /// <returns>重新链接的文件数</returns>
    public int RelinkSharedInstallFiles(GameInstallContext context, string source, List<string> paths)
    {
        int count = 0;
        foreach (string path in paths)
        {
            string temp = path + ".link";
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
                if (!Kernel32.CreateHardLink(temp, source))
                {
                    _logger.LogWarning("Failed to create hard link {temp} to repaired file {source}, error: {error}", temp, source, Marshal.GetLastPInvokeError());
                    continue;
                }
                File.Move(temp, path, true);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to relink {path} to repaired file {source}", path, source);
                try
                {
                    File.Delete(temp);
                }
                catch { }
            }
        }
        if (count > 0)
        {
            context.IncrementSharedInstallRelinkedFileCount(count);
        }
        return count;
    }



    /// <summary>
    /// 由其他区服中的硬链接路径推出它的游戏目录：路径末尾须是本区服这个文件的相对路径。
    /// 原神国服（含 B 服）的数据目录是 YuanShen_Data，国际服是 GenshinImpact_Data，硬链接安装时按对方的目录名对应，两种都要试。
    /// </summary>
    /// <param name="path">其他区服中的硬链接完整路径</param>
    /// <param name="relativePath">本区服这个文件相对于游戏目录的路径</param>
    /// <returns>对方的游戏目录；对不上时为 <see langword="null"/>，说明不是按游戏目录结构链接的，不去动它</returns>
    private static string? GetSharedInstallRoot(string path, string relativePath)
    {
        const string YuanShen_Data = nameof(YuanShen_Data);
        const string GenshinImpact_Data = nameof(GenshinImpact_Data);
        string relative = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        List<string> candidates = [relative];
        if (relative.StartsWith(YuanShen_Data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(GenshinImpact_Data + relative[YuanShen_Data.Length..]);
        }
        else if (relative.StartsWith(GenshinImpact_Data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(YuanShen_Data + relative[GenshinImpact_Data.Length..]);
        }
        foreach (string candidate in candidates)
        {
            string suffix = Path.DirectorySeparatorChar + candidate;
            if (path.Length > suffix.Length && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^suffix.Length];
            }
        }
        return null;
    }



    /// <summary>
    /// 其他区服的游戏目录能否一并修复：config.ini 记录的是同一游戏，版本与本次修复的目标版本相同。结果按目录缓存在任务上。
    /// </summary>
    private bool IsSharedInstallRepairable(GameInstallContext context, string root)
    {
        return context.SharedInstallRoots.GetOrAdd(root, folder =>
        {
            string? version = null, biz = null;
            try
            {
                string config = Path.Join(folder, "config.ini");
                if (File.Exists(config))
                {
                    // 与读取本地版本一致，重复的键以最后一个为准
                    foreach (string line in File.ReadLines(config))
                    {
                        if (line.StartsWith("game_version=", StringComparison.OrdinalIgnoreCase))
                        {
                            version = line["game_version=".Length..].Trim();
                        }
                        else if (line.StartsWith("game_biz=", StringComparison.OrdinalIgnoreCase))
                        {
                            biz = line["game_biz=".Length..].Trim();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to read config.ini of hard-linked install {root}", folder);
            }
            bool repairable = Version.TryParse(version, out Version? local)
                && Version.TryParse(context.TargetGameVersion, out Version? latest)
                && local == latest
                && !string.IsNullOrWhiteSpace(biz)
                && new GameBiz(biz).Game == context.GameId.GameBiz.Game;
            _logger.LogInformation("Hard-linked install {root} ({biz}, {version}) shares files, repair together: {repairable}", folder, biz, version, repairable);
            return repairable;
        });
    }


}
