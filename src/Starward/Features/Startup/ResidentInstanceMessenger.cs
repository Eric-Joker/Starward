using Microsoft.UI.Dispatching;
using Serilog;
using Starward.Core;
using System;
using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace Starward.Features.Startup;

/// <summary>
/// 常驻实例（系统托盘）与「点击系统通知后由协议拉起的短命进程」之间的单向通知通道。
/// <para>
/// 常驻侧创建一个 message-only 窗口（父窗口为 <c>HWND_MESSAGE</c>），客户端用 <c>FindWindowEx</c> 找到它后
/// 以 <c>WM_COPYDATA</c> 投递「打开主窗口并切到某游戏」，常驻侧收到后排到 UI 队列里执行，
/// 由 <see cref="App.EnsureMainWindow(GameBiz?)"/> 完成切游戏与显示窗口。
/// </para>
/// <para>
/// 为什么不用 <c>AppInstance.RedirectActivationToAsync</c> 转发整个激活：通知平台总以普通权限拉起本进程，
/// 而常驻实例可能是提权的（免 UAC 快捷方式 / 以管理员运行）。转发跨完整性级别不可靠，
/// 而 UIPI 允许「高 → 低」发窗口消息，反向再由接收方放行 <c>WM_COPYDATA</c>（见 <see cref="StartListening"/>）。
/// 另外还需要 <c>AllowSetForegroundWindow</c> 把发送方手里的前台权转给常驻实例，否则它激活的主窗口只会在任务栏闪烁。
/// </para>
/// <para>
/// 移植自 TZ 分支的 Moonward 实现（3b760d1e），按本仓库只保留「打开主窗口」一种消息：
/// 本分支没有「快捷方式启动游戏」需要转交给常驻实例的场景（单实例激活重定向已覆盖）。
/// </para>
/// </summary>
internal static partial class ResidentInstanceMessenger
{

    /// <summary>message-only 窗口的标题，客户端据此定位常驻实例。窗口句柄本身按会话隔离，无需再加用户/会话后缀。</summary>
    private const string WindowName = "Starward.ResidentIpc";

    /// <summary>借用系统预定义的 STATIC 窗口类，省去注册自定义窗口类；行为由子类化过程接管。</summary>
    private const string WindowClass = "STATIC";

    private const uint WM_COPYDATA = 0x004A;

    /// <summary>message-only 窗口的父窗口伪句柄。</summary>
    private const nint HWND_MESSAGE = -3;

    /// <summary><c>COPYDATASTRUCT.dwData</c> 取值：打开主窗口（点击了系统通知），负载为 <c>{game_biz}|{uid}</c>，两段都可为空 / 0。</summary>
    private const nint MSG_OPEN_MAIN_WINDOW = 1;

    private const uint MSGFLT_ALLOW = 1;

    private const uint SMTO_ABORTIFHUNG = 0x0002;


    private static nint _windowHandle;

    /// <summary>子类化过程必须由托管侧持有引用，否则会被 GC 回收，回调时变成野指针。</summary>
    private static ComCtl32.SUBCLASSPROC? _subclassProc;


    /// <summary>
    /// 在当前线程（必须是有消息循环的 UI 线程）创建 message-only 窗口并开始接收通知。
    /// 幂等：重复调用只创建一次。
    /// </summary>
    public static void StartListening()
    {
        if (_windowHandle != 0)
        {
            return;
        }
        try
        {
            _windowHandle = CreateWindowEx(0, WindowClass, WindowName, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
            if (_windowHandle == 0)
            {
                Log.Warning("Create resident IPC window failed: {error}", Marshal.GetLastPInvokeError());
                return;
            }
            // 本进程以管理员运行时，普通权限的协议进程发来的 WM_COPYDATA 会被 UIPI 拦下，需显式放行
            ChangeWindowMessageFilterEx(_windowHandle, WM_COPYDATA, MSGFLT_ALLOW, 0);
            _subclassProc = new ComCtl32.SUBCLASSPROC(SubclassProc);
            ComCtl32.SetWindowSubclass(_windowHandle, _subclassProc, 1003, IntPtr.Zero);
            Log.Information("Resident IPC listener started.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start resident IPC listener");
        }
    }


    /// <summary>
    /// 销毁 message-only 窗口，停止接收通知。随常驻实例退出调用。
    /// </summary>
    public static void StopListening()
    {
        if (_windowHandle == 0)
        {
            return;
        }
        try
        {
            DestroyWindow(_windowHandle);
        }
        catch { }
        _windowHandle = 0;
        _subclassProc = null;
    }


    /// <summary>
    /// 当前系统会话中是否已有常驻实例在接收通知。
    /// </summary>
    /// <returns>找到 message-only 窗口返回 <see langword="true"/>。</returns>
    public static bool IsResidentInstanceListening()
    {
        return FindResidentWindow() != 0;
    }


    /// <summary>
    /// 通知常驻实例：用户点了系统通知，请打开主窗口并切到对应游戏。
    /// </summary>
    /// <param name="biz">要切到的游戏区服，空则只打开主窗口。</param>
    /// <param name="uid">触发通知的角色 uid，0 表示不指定。</param>
    /// <returns>已成功投递返回 <see langword="true"/>；未找到常驻实例或投递失败返回 <see langword="false"/>。</returns>
    public static bool NotifyOpenMainWindow(GameBiz? biz, long uid)
    {
        nint target = FindResidentWindow();
        if (target == 0)
        {
            Log.Information("Resident IPC: no resident instance is listening; continue a normal startup.");
            return false;
        }
        try
        {
            // 本进程由用户点击通知拉起，持有前台权；转给常驻实例，否则它激活的主窗口只会在任务栏闪烁
            if (GetWindowThreadProcessId(target, out uint residentPid) != 0 && residentPid != 0)
            {
                AllowSetForegroundWindow(residentPid);
            }
            SendCopyData(target, MSG_OPEN_MAIN_WINDOW, $"{biz}|{uid}");
            Log.Information("Resident IPC: asked the resident instance to open the main window ({biz}, {uid}).", biz, uid);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Notify resident instance to open main window ({biz}, {uid})", biz, uid);
            return false;
        }
    }


    /// <summary>
    /// 以 <c>WM_COPYDATA</c> 同步投递一段字符串负载。
    /// </summary>
    /// <param name="target">常驻实例的 message-only 窗口。</param>
    /// <param name="kind">消息种类（<c>COPYDATASTRUCT.dwData</c>）。</param>
    /// <param name="payload">负载字符串。</param>
    private static void SendCopyData(nint target, nint kind, string payload)
    {
        nint buffer = Marshal.StringToHGlobalUni(payload);
        try
        {
            var data = new COPYDATASTRUCT
            {
                dwData = kind,
                // 含结尾终止符，接收端按 cbData 反算字符数时一并裁掉
                cbData = (payload.Length + 1) * sizeof(char),
                lpData = buffer,
            };
            // WM_COPYDATA 必须同步发送（数据指针要在处理期间有效）；限时 3 秒，避免常驻实例卡住时拖住发送方
            SendMessageTimeout(target, WM_COPYDATA, 0, ref data, SMTO_ABORTIFHUNG, 3000, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }


    /// <summary>
    /// 找到常驻实例的 message-only 窗口；没有时返回 0。
    /// </summary>
    /// <returns>窗口句柄。</returns>
    private static nint FindResidentWindow()
    {
        try
        {
            return FindWindowEx(HWND_MESSAGE, 0, WindowClass, WindowName);
        }
        catch
        {
            return 0;
        }
    }


    /// <summary>
    /// 常驻实例 message-only 窗口的窗口过程。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="uMsg">消息。</param>
    /// <param name="wParam">wParam。</param>
    /// <param name="lParam">lParam。</param>
    /// <param name="uIdSubclass">子类化 Id。</param>
    /// <param name="dwRefData">子类化引用数据。</param>
    /// <returns>处理结果。</returns>
    private static IntPtr SubclassProc(HWND hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_COPYDATA)
        {
            try
            {
                COPYDATASTRUCT data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (data.lpData != 0 && data.cbData > sizeof(char) && data.dwData == MSG_OPEN_MAIN_WINDOW)
                {
                    string payload = Marshal.PtrToStringUni(data.lpData, data.cbData / sizeof(char) - 1) ?? "";
                    OnOpenMainWindowMessageReceived(payload);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Handle resident IPC message");
            }
            return 1;
        }
        return ComCtl32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }


    /// <summary>
    /// 处理「打开主窗口」通知：排到 UI 队列里再做，让发送方的同步 <c>SendMessageTimeout</c> 立即返回，不必等主窗口建好。
    /// </summary>
    /// <param name="payload">负载，格式 <c>{game_biz}|{uid}</c>，game_biz 可为空。</param>
    private static void OnOpenMainWindowMessageReceived(string payload)
    {
        string[] parts = payload.Split('|');
        GameBiz? biz = parts.Length > 0 && GameBiz.TryParse(parts[0], out GameBiz parsed) ? parsed : null;
        Log.Information("Resident IPC: open main window requested by notification ({biz}).", biz);
        // 子类化过程跑在托盘窗口的 UI 线程上
        DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            Log.Warning("Resident IPC: no dispatcher queue on the current thread; open main window skipped.");
            return;
        }
        dispatcherQueue.TryEnqueue(() => App.Current.EnsureMainWindow(biz));
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nint dwData;
        public int cbData;
        public nint lpData;
    }


    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
                                               int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string lpszClass, string lpszWindow);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, ref COPYDATASTRUCT lParam,
                                                   uint fuFlags, uint uTimeout, out nint lpdwResult);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "ChangeWindowMessageFilterEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint pChangeFilterStruct);

}
