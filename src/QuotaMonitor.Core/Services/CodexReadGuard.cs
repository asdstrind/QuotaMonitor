namespace QuotaMonitor.Core.Services;
/// <summary>Transient Windows sharing protection. Does not read credential bytes or modify files/ACLs.</summary>
public sealed class CodexReadGuard : IDisposable
{
    private readonly List<FileStream> handles=[];
    public CodexReadGuard(string home)
    {
        try{foreach(var name in new[]{"config.toml","auth.json"}){var file=Path.Combine(home,name);if(File.Exists(file))handles.Add(new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read));}}
        catch{Dispose();throw;}
    }
    public void Dispose(){foreach(var handle in handles)handle.Dispose();handles.Clear();}
}
