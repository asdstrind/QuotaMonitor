using Microsoft.Win32;
namespace QuotaMonitor.Services;
public static class StartupService
{
    private const string Key="Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public static bool IsEnabled(){using var key=Registry.CurrentUser.OpenSubKey(Key);return key?.GetValue("QuotaMonitor") is string;}
    public static string? ReadCommand(){using var key=Registry.CurrentUser.OpenSubKey(Key);return key?.GetValue("QuotaMonitor") as string;}
    public static void RestoreCommand(string? command){using var key=Registry.CurrentUser.CreateSubKey(Key);if(command is null)key.DeleteValue("QuotaMonitor",false);else key.SetValue("QuotaMonitor",command);}
    public static void Set(bool enabled){using var key=Registry.CurrentUser.CreateSubKey(Key);if(enabled){var executable=Environment.ProcessPath??throw new IOException("无法定位应用路径。");key.SetValue("QuotaMonitor","\""+executable+"\"");}else key.DeleteValue("QuotaMonitor",false);}
}
