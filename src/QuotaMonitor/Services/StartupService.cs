using Microsoft.Win32;
namespace QuotaMonitor.Services;
public static class StartupService
{
    public const string ValueName="QuotaMonitor";
    private const string DefaultRunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultApprovedKey=@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    // Verification points these at an isolated HKCU subtree; the shipped app always uses the
    // real Run and StartupApproved keys, and only Set/Restore ever write them.
    private static string runKey=DefaultRunKey;
    private static string approvedKey=DefaultApprovedKey;

    internal static void UseIsolatedStore(string root){runKey=root+@"\Run";approvedKey=root+@"\StartupApproved\Run";}
    internal static void UseProductionStore(){runKey=DefaultRunKey;approvedKey=DefaultApprovedKey;}

    // Full registration state for exact rollback: raw Run command plus the raw StartupApproved
    // marker Windows writes when the user disables the item in Task Manager.
    public sealed record StartupState(string? Command,byte[]? Approved);

    public static string? ReadCommand()
    {
        using var key=Registry.CurrentUser.OpenSubKey(runKey);
        return key?.GetValue(ValueName) as string;
    }

    // The current process executable in the quoted form Windows expects in Run.
    public static string? CurrentCommand()
    {
        var executable=Environment.ProcessPath;
        if(!TryFullPath(executable,out var full)||!File.Exists(full))return null;
        return "\""+full+"\"";
    }

    // Run values written by this app are quoted. An unquoted or argument-bearing command is
    // never treated as a valid current registration; path repair rewrites it.
    public static string? CommandExecutable(string? command)
    {
        if(string.IsNullOrWhiteSpace(command))return null;
        var text=command.Trim();
        if(text.Length<3||text[0]!='"')return null;
        var end=text.IndexOf('"',1);
        return end>1&&string.IsNullOrWhiteSpace(text[(end+1)..])&&Path.IsPathFullyQualified(text[1..end])?text[1..end]:null;
    }

    public static bool HasCurrentCommand()
    {
        var registered=CommandExecutable(ReadCommand());
        if(registered is null||!TryFullPath(registered,out var registeredFull))return false;
        if(!TryFullPath(Environment.ProcessPath,out var executableFull))return false;
        return string.Equals(registeredFull,executableFull,StringComparison.OrdinalIgnoreCase)&&File.Exists(registeredFull);
    }

    // StartupApproved\Run is where Windows records a Task Manager disable. A byte with bit 0
    // set (0x03/0x07) means disabled; 0x02/0x06, a missing value, or an absent key means enabled.
    private static byte[]? ReadApproved()
    {
        using var key=Registry.CurrentUser.OpenSubKey(approvedKey);
        return key?.GetValue(ValueName) as byte[];
    }
    private static bool ApprovedDisabled(byte[]? approved)=>approved is{Length:>0}&&(approved[0]&1)!=0;

    // Enabled means: the Run value is the quoted current executable that still exists, and
    // Windows itself has not disabled that named value.
    public static bool IsEnabled()=>HasCurrentCommand()&&!ApprovedDisabled(ReadApproved());

    public static StartupState Capture()=>new(ReadCommand(),ReadApproved());

    public static void Set(bool enabled){if(enabled)Enable();else Disable();}

    private static void Enable()
    {
        var command=CurrentCommand()??throw new InvalidOperationException("无法定位当前应用路径，不能启用开机启动。");
        try
        {
            using(var key=Registry.CurrentUser.CreateSubKey(runKey,true))
                key.SetValue(ValueName,command,RegistryValueKind.String);
        }
        catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("无法写入开机启动项；请检查当前用户注册表权限。",ex);
        }
        // An explicit enable also clears Windows' own disable marker for this named value only.
        ClearDisabled();
        var registered=ReadCommand();
        if(!string.Equals(registered,command,StringComparison.Ordinal))
            throw new InvalidOperationException("开机启动项写入后回读不一致，Windows 可能阻止了修改；未声称已启用。");
        var executable=CommandExecutable(registered);
        if(executable is null||!File.Exists(executable))
            throw new InvalidOperationException("开机启动项指向的应用路径不存在；未声称已启用。");
        if(ApprovedDisabled(ReadApproved()))
            throw new InvalidOperationException("Windows 已禁用 QuotaMonitor 开机启动（任务管理器 > 启动）；应用未声称已启用，请在 Windows 中启用后重试。");
    }

    private static void Disable()
    {
        try
        {
            // Remove only this app's named value, never other entries in the Run key.
            using var key=Registry.CurrentUser.OpenSubKey(runKey,true);
            key?.DeleteValue(ValueName,false);
        }
        catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("无法移除开机启动项；请检查当前用户注册表权限。",ex);
        }
        if(ReadCommand() is not null)
            throw new InvalidOperationException("开机启动项仍然存在，Windows 可能阻止了修改；未声称已禁用。");
    }

    private static void ClearDisabled()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(approvedKey,true);
            if(key?.GetValue(ValueName) is byte[] marker&&ApprovedDisabled(marker))key.DeleteValue(ValueName,false);
        }
        catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A policy-protected marker is reported by the readback check in Enable().
        }
    }

    // App-launch path repair: when the user setting is enabled and the stored command is stale
    // or missing, rewrite it to the current executable. It deliberately does not override a
    // Windows "disabled" marker (the user's Task Manager choice).
    public static bool RepairAtLaunch(bool enabled)
    {
        if(!enabled||HasCurrentCommand())return false;
        var command=CurrentCommand()??throw new InvalidOperationException("无法定位当前应用路径，不能修复开机启动路径。");
        try
        {
            using(var key=Registry.CurrentUser.CreateSubKey(runKey,true))
                key.SetValue(ValueName,command,RegistryValueKind.String);
        }
        catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("无法修复开机启动路径；请检查当前用户注册表权限。",ex);
        }
        var registered=ReadCommand();
        if(!string.Equals(registered,command,StringComparison.Ordinal))
            throw new InvalidOperationException("开机启动路径修复后回读不一致；未声称已修复。");
        var executable=CommandExecutable(registered);
        if(executable is null||!File.Exists(executable))
            throw new InvalidOperationException("开机启动路径修复失败：当前应用路径不存在。");
        return true;
    }

    // Compatibility rollback for callers that only captured ReadCommand(): restore the raw
    // command value exactly. Restore(StartupState) also brings back the disabled marker.
    public static void RestoreCommand(string? command)
    {
        if(command is null)
        {
            using var key=Registry.CurrentUser.OpenSubKey(runKey,true);
            key?.DeleteValue(ValueName,false);
            return;
        }
        using var created=Registry.CurrentUser.CreateSubKey(runKey,true);
        created.SetValue(ValueName,command,RegistryValueKind.String);
    }

    public static void Restore(StartupState state)
    {
        RestoreCommand(state.Command);
        try
        {
            if(state.Approved is null)
            {
                using var key=Registry.CurrentUser.OpenSubKey(approvedKey,true);
                key?.DeleteValue(ValueName,false);
            }
            else
            {
                using var key=Registry.CurrentUser.CreateSubKey(approvedKey,true);
                key.SetValue(ValueName,state.Approved,RegistryValueKind.Binary);
            }
        }
        catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("无法恢复 Windows 启动项禁用状态；请检查任务管理器 > 启动。",ex);
        }
    }

    private static bool TryFullPath(string? path,out string full)
    {
        full="";
        if(string.IsNullOrWhiteSpace(path))return false;
        try{full=Path.GetFullPath(path);return true;}
        catch(Exception ex)when(ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException){return false;}
    }
}
