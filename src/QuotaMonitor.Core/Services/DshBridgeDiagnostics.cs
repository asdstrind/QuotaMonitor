namespace QuotaMonitor.Core.Services;

// Bounded, local event codes only. Never log frames, balances, identities or exceptions.
internal static class DshBridgeDiagnostics
{
    private static readonly object Gate=new();
    private static string? last;
    public static void Write(string code)
    {
        lock(Gate) {
            if(last==code)return;
            last=code;
            try {
                var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuotaMonitor");
                Directory.CreateDirectory(directory);
                var file=Path.Combine(directory,"dsh-bridge.log");
                if(File.Exists(file)&&new FileInfo(file).Length>65536)File.WriteAllText(file,"");
                File.AppendAllText(file,$"{DateTimeOffset.UtcNow:O} {code}{Environment.NewLine}");
            }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        }
    }
}
