using Microsoft.Win32;

namespace Starward.Setup.Services;

public static class RegistryHelper
{

    public static void WriteUninstallInfo(string folder, string version, long size)
    {
        string exe = Path.Combine(folder, "Starward.exe");
        string setupExe = Path.Combine(folder, "Starward.Setup.exe");
        using var subkey = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Starward");
        subkey.SetValue("Publisher", "Scighost", RegistryValueKind.String);
        subkey.SetValue("DisplayName", "Starward", RegistryValueKind.String);
        subkey.SetValue("DisplayIcon", exe, RegistryValueKind.String);
        subkey.SetValue("DisplayVersion", version, RegistryValueKind.String);
        subkey.SetValue("InstallLocation", folder, RegistryValueKind.String);
        subkey.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        subkey.SetValue("InstallDate", $"{DateTime.Now:yyyyMMdd}", RegistryValueKind.String);
        subkey.SetValue("UninstallString", $"\"{setupExe}\" uninstall", RegistryValueKind.String);
        subkey.SetValue("QuietUninstallString", $"\"{setupExe}\" uninstall /S", RegistryValueKind.String);
    }


    public static void DeleteUninstallInfo()
    {
        Registry.LocalMachine.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Starward", false);
    }


    public static void WriteUrlProtocol(string folder)
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Starward", false);
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\Starward", "", "URL:Starward Protocol");
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\Starward", "URL Protocol", "");
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\Starward\DefaultIcon", "", "Starward.exe,1");
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\Starward\Shell\Open\Command", "", $"""
            "{Path.Combine(folder, "Starward.exe")}" "%1"
            """);
    }


    public static void DeleteUrlProtocol()
    {
        Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\Starward", false);
    }


    /// <summary>
    /// 删除系统通知登记：通知身份（AUMID）、点击通知时激活用的协议，以及系统为它建的通知设置键。
    /// <para>
    /// 应用侧对应 <c>Features/Notification/SystemNotificationService</c>，但 SetUp 工程不引用 app 的类型，
    /// 所以两套身份与协议名在这里复制一份；安装版与调试版各一套（Debug 用 <c>Starward.Debug</c> / <c>starward-notify-dev</c>），
    /// 两套都清。协议与身份都登记在 HKCU，<c>starward://</c> 那个由 <see cref="DeleteUrlProtocol"/> 处理。
    /// </para>
    /// </summary>
    public static void DeleteNotificationRegistrations()
    {
        foreach (string appUserModelId in new[] { "Starward", "Starward.Debug" })
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{appUserModelId}", false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{appUserModelId}", false);
        }
        foreach (string scheme in new[] { "starward-notify", "starward-notify-dev" })
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{scheme}", false);
        }
    }


    public static string? GetInstallLocation()
    {
        using var subkey = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Starward");
        return subkey?.GetValue("InstallLocation") as string;
    }


    public static void DeleteRegistrySetting()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Starward", false);
    }

}





