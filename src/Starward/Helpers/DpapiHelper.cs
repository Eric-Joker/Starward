using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Starward.Helpers;

/// <summary>
/// 使用 Windows DPAPI（当前用户范围）保护本地保存的敏感数据。
/// <para>
/// 令牌等凭据只保存在本机，并且只有当前 Windows 用户能够解密；
/// 直接把数据库文件复制到其它账号或其它机器上都无法还原明文。
/// </para>
/// </summary>
public static partial class DpapiHelper
{

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;


    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }


    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);


    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);


    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);




    /// <summary>
    /// 加密文本。空内容返回 null，加密失败时抛出异常由调用方决定是否降级。
    /// </summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }
        byte[] input = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(Transform(input, protect: true));
    }




    /// <summary>
    /// 解密文本。数据不是由当前用户加密时返回 null（调用方可以据此回退到旧格式）。
    /// </summary>
    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return null;
        }
        try
        {
            byte[] input = Convert.FromBase64String(protectedText);
            byte[] output = Transform(input, protect: false);
            return Encoding.UTF8.GetString(output);
        }
        catch
        {
            return null;
        }
    }




    private static byte[] Transform(byte[] input, bool protect)
    {
        var dataIn = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, dataIn.Data, input.Length);
            bool ok = protect
                ? CryptProtectData(ref dataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out dataOut);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                byte[] result = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
                return result;
            }
            finally
            {
                LocalFree(dataOut.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataIn.Data);
        }
    }


}
