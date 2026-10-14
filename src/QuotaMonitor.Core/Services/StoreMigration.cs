namespace QuotaMonitor.Core.Services;

/// <summary>
/// Copies this application's own previously saved non-secret store files into a new store folder.
/// Windows can redirect %LOCALAPPDATA% for a process launched from a packaged app, so an earlier
/// save may sit under a package LocalCache. Only this application's own folders are inspected and
/// only settings/snapshot/history files are copied; credentials are never read or copied.
/// </summary>
public static class StoreMigration
{
    public static readonly IReadOnlyList<string> Files = ["settings.json", "snapshots.json", "history.json"];

    public static IEnumerable<string> CandidateFolders(string localApplicationData)
    {
        var packages = Path.Combine(localApplicationData, "Packages");
        if (Directory.Exists(packages))
        {
            string[] folders;
            try { folders = Directory.GetDirectories(packages); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folders = []; }
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, "LocalCache", "Local", "QuotaMonitor");
                if (Directory.Exists(candidate)) yield return candidate;
            }
        }
        yield return Path.Combine(localApplicationData, "QuotaMonitor");
    }

    /// <summary>Returns how many files were copied, never overwriting an existing destination file.</summary>
    public static int Import(string localApplicationData, string destination, int budget)
    {
        var copied = 0;
        var target = Path.GetFullPath(destination);
        foreach (var source in CandidateFolders(localApplicationData))
        {
            if (copied >= budget) break;
            if (string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in Files)
            {
                if (copied >= budget) break;
                var destinationFile = Path.Combine(target, name);
                if (File.Exists(destinationFile) || !File.Exists(Path.Combine(source, name))) continue;
                try { File.Copy(Path.Combine(source, name), destinationFile, false); copied++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return copied;
    }
}
