using System.Text.Json;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
namespace QuotaMonitor.Configuration;
public sealed class SettingsStore
{
    public string Root {get;}
    public string? LoadWarning {get;private set;}
    private static readonly JsonSerializerOptions Options=new(){WriteIndented=true};
    public SettingsStore(string? root=null){Root=root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuotaMonitor");Directory.CreateDirectory(Root);}
    public MonitorSettings Load(){try{return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(Path.Combine(Root,"settings.json")))??new();}catch(FileNotFoundException){return new();}catch(Exception ex)when(ex is IOException or JsonException){LoadWarning="设置读取失败，已使用默认设置，原文件未改写。";return new();}}
    public IReadOnlyList<ProviderSnapshot> LoadCache(){try{return JsonSerializer.Deserialize<ProviderSnapshot[]>(File.ReadAllText(Path.Combine(Root,"snapshots.json")))??[];}catch(Exception ex)when(ex is IOException or JsonException){return [];}}
    public IReadOnlyList<UsageHistory> LoadHistory(){try{return JsonSerializer.Deserialize<UsageHistory[]>(File.ReadAllText(Path.Combine(Root,"history.json")))??[];}catch(Exception ex)when(ex is IOException or JsonException){return [];}}
    // Daily aggregates only (counts, costs, coverage); no credentials or conversation content.
    public void SaveHistory(IReadOnlyList<UsageHistory> history)=>Atomic("history.json",history);
    public void Save(MonitorSettings settings)=>Atomic("settings.json",settings);
    public void SaveCache(IReadOnlyList<ProviderSnapshot> snapshots)=>Atomic("snapshots.json",snapshots);
    private void Atomic(string name,object value){var target=Path.Combine(Root,name);var temp=target+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(value,Options));if(name=="settings.json"&&File.Exists(target))File.Replace(temp,target,target+".bak");else File.Move(temp,target,true);}
    /// <summary>Copies this application's own earlier non-secret store files; see StoreMigration.</summary>
    public static int ImportPreviousStore(string destination,int budget)=>StoreMigration.Import(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),destination,budget);
}
