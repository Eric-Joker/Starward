using Microsoft.Extensions.Logging;
using Starward.Helpers;
using System;
using System.IO;

namespace Starward.Features.Setting;

/// <summary>
/// 开机自启。
/// <para>
/// 不写注册表 <c>Run</c> 键，改成在用户「启动」文件夹里放一个快捷方式：
/// 位置对用户可见、能直接删掉，也会出现在任务管理器的「启动」页里由用户随时关掉，
/// 卸载后残留的入口更容易被发现和清理。命令行固定带 <c>--hide</c>，即开机后只驻留托盘。
/// </para>
/// </summary>
internal static class AutoStartService
{

#if DEBUG
    private const string ShortcutFileName = "Starward.Debug.lnk";
#else
    private const string ShortcutFileName = "Starward.lnk";
#endif


    /// <summary>启动文件夹里的快捷方式完整路径。</summary>
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutFileName);


    /// <summary>缓存的日志记录器，首次使用时创建。</summary>
    private static ILogger? _logger;


    /// <summary>
    /// 取本类的日志记录器。静态类不能作为 <see cref="ILogger{T}"/> 的类型参数，故按名称创建。
    /// </summary>
    private static ILogger GetLogger()
    {
        return _logger ??= AppConfig.GetService<ILoggerFactory>().CreateLogger(nameof(AutoStartService));
    }


    /// <summary>可移动存储上的程序不提供自启：盘符换了快捷方式就会失效。</summary>
    public static bool IsAvailable => !AppConfig.IsAppInRemovableStorage;


    /// <summary>快捷方式是否存在，即当前是否已开启开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            return File.Exists(ShortcutPath);
        }
        catch (Exception ex)
        {
            GetLogger().LogWarning(ex, "Read start-at-login state");
            return false;
        }
    }


    /// <summary>创建或覆盖启动快捷方式。</summary>
    /// <exception cref="InvalidOperationException">可移动存储上不可用，或拿不到当前程序路径。</exception>
    public static void Enable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Start at login is not available on removable storage.");
        }
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("Can not determine the current executable path.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        ShellLinkHelper.Create(
            ShortcutPath,
            exe,
            arguments: "--hide",
            workingDirectory: AppContext.BaseDirectory,
            iconPath: exe,
            description: "Starward");
    }


    /// <summary>删除启动快捷方式。不存在时静默返回。</summary>
    public static void Disable()
    {
        try
        {
            File.Delete(ShortcutPath);
        }
        catch (Exception ex)
        {
            GetLogger().LogWarning(ex, "Remove start-at-login shortcut");
        }
    }


    /// <summary>
    /// 启动时调用：已经开启自启、但快捷方式指向的不是当前这份程序（换了目录、或应用更新过）时重建一次。
    /// 未开启时不做任何事，避免平白写用户目录。
    /// </summary>
    public static void RepairIfNeeded()
    {
        try
        {
            if (!IsAvailable || !File.Exists(ShortcutPath))
            {
                return;
            }
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }
            if (ShellLinkHelper.TryGetTarget(ShortcutPath, out string target)
                && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            GetLogger().LogInformation("Repair start-at-login shortcut");
            Enable();
        }
        catch (Exception ex)
        {
            GetLogger().LogWarning(ex, "Repair start-at-login shortcut");
        }
    }

}
