using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
namespace QuotaMonitor.Core.Services;
public sealed class RefreshCoordinator : IAsyncDisposable
{
    private sealed class Slot(IProviderAdapter adapter,ProviderSnapshot snapshot)
    {
        public IProviderAdapter Adapter {get;}=adapter;
        public ProviderSnapshot Snapshot {get;set;}=snapshot;
        public DateTimeOffset Next {get;set;}=DateTimeOffset.MinValue;
        public int Failures {get;set;}
        public SemaphoreSlim Gate {get;}=new(1);
    }
    private readonly MonitorSettings settings;
    private readonly Slot[] slots;
    private readonly CancellationTokenSource stop=new();
    private Task? loop;
    public event Action<ProviderSnapshot>? Updated;
    public RefreshCoordinator(MonitorSettings settings,IEnumerable<IProviderAdapter> adapters,IEnumerable<ProviderSnapshot>? cached=null)
    {
        this.settings=settings;slots=adapters.Select(a=>new Slot(a,cached?.FirstOrDefault(x=>x.ProviderId==a.Id)??ProviderSnapshot.Empty(a.Id,a.Id switch{"codex"=>"Codex","claude"=>"ClaudeCN","antigravity"=>"Antigravity",_=>"DSH"},a.Id is "codex" or "antigravity"?MetricType.Quota:a.Id=="claude"?MetricType.Balance:MetricType.Tokens))).ToArray();
    }
    public void Start(){loop=RunAsync();}
    private async Task RunAsync(){try{await RefreshAsync(false);using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));while(await timer.WaitForNextTickAsync(stop.Token))await RefreshAsync(false);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}}
    public Task RefreshAsync(bool force)=>Task.WhenAll(slots.Select(s=>RefreshSlotAsync(s,force)));
    private async Task RefreshSlotAsync(Slot slot,bool force)
    {
        if(!await slot.Gate.WaitAsync(0,stop.Token))return;
        try {
            if(!settings.Enabled(slot.Adapter.Id)){slot.Snapshot=slot.Snapshot with{ConnectionStatus=ConnectionStatus.Disabled};Updated?.Invoke(slot.Snapshot);return;}
            if(!force&&DateTimeOffset.UtcNow<slot.Next)return;
            try {slot.Snapshot=await slot.Adapter.RefreshAsync(force,stop.Token);slot.Failures=slot.Snapshot.ConnectionStatus is ConnectionStatus.Unavailable or ConnectionStatus.Partial?slot.Failures+1:0;}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
            catch(Exception ex){var safe=ex is ProviderException?ex.Message:ex is OperationCanceledException?"请求超时，请稍后刷新。":ex is HttpRequestException?"网络连接失败，请检查网络。":"读取失败（"+ex.GetType().Name+"），详情正文已隐藏。";slot.Snapshot=slot.Snapshot with{ConnectionStatus=ConnectionStatus.Unavailable,ErrorMessage=safe};slot.Failures++;}
            // DSH's account snapshot arrives independently from its local usage scan. A Partial
            // historical session must not push wallet synchronization beyond its stale threshold.
            slot.Next=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(Math.Min(1800,settings.Interval(slot.Adapter.Id).TotalSeconds*Math.Pow(2,slot.Adapter.Id=="dsh"?0:Math.Min(slot.Failures,3))));
            Updated?.Invoke(slot.Snapshot);
        }finally{slot.Gate.Release();}
    }
    public IReadOnlyList<ProviderSnapshot> Snapshots=>slots.Select(x=>x.Snapshot).ToArray();
    public async ValueTask DisposeAsync(){stop.Cancel();if(loop is not null)try{await loop;}catch(OperationCanceledException){}foreach(var s in slots){await s.Gate.WaitAsync();s.Gate.Release();s.Gate.Dispose();}stop.Dispose();}
}
