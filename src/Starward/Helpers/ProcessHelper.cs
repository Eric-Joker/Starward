using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Starward.Helpers;

/// <summary>
/// <see cref="Process"/> 覆盖不到的进程信息查询。
/// </summary>
internal static partial class ProcessHelper
{


    /// <summary>
    /// 进程的 exe 完整路径。
    /// </summary>
    /// <param name="process">进程。</param>
    /// <returns>exe 完整路径；进程已退出或无权访问时返回 <see langword="null"/>。</returns>
    public static string? TryGetProcessImagePath(Process process)
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // 游戏或启动程序以管理员权限运行时，Process.MainModule 要读进程内存会被拒绝；查询受限信息的权限就足够取路径
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Id);
            if (handle == IntPtr.Zero)
            {
                return null;
            }
            const int bufferLength = 1024;
            buffer = Marshal.AllocHGlobal(bufferLength * sizeof(char));
            uint size = bufferLength;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size == 0)
            {
                return null;
            }
            string? path = Marshal.PtrToStringUni(buffer, (int)Math.Min(size, (uint)bufferLength));
            return path is null ? null : Path.GetFullPath(path.TrimEnd('\0'));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }



    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION</summary>
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;



    #region Native

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    /// <summary>QueryFullProcessImageNameW</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, IntPtr lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    #endregion

}
