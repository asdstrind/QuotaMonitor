using System.Text.Json;

namespace QuotaMonitor.Services;

// A small local receipt makes login-time initialization failures diagnosable without secrets.
internal static class StartupHealth
{
    public static void Record(string stage, string? exceptionType = null)
    {
        try
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuotaMonitor");
            Directory.CreateDirectory(root);
            var path=Path.Combine(root,"startup-health.json");
            var receipt=new{AtUtc=DateTimeOffset.UtcNow,Version=typeof(StartupHealth).Assembly.GetName().Version?.ToString(3),Stage=stage,ExceptionType=exceptionType,ExecutableExists=File.Exists(Environment.ProcessPath),CurrentRunCommand=StartupService.HasCurrentCommand(),StartupEnabled=StartupService.IsEnabled()};
            File.WriteAllText(path,JsonSerializer.Serialize(receipt));
        }
        catch { /* Logging never prevents startup. */ }
    }
}
