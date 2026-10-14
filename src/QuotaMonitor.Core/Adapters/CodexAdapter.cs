using System.Diagnostics;
using System.Text.Json;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Core.Adapters;
public sealed class CodexAdapter(MonitorSettings settings) : IProviderAdapter
{
    public string Id => "codex";
    public static string? DiscoverExecutable()
    {
        var directories=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator);
        foreach(var dir in directories) {
            var direct=Path.Combine(dir,"codex.exe"); if(File.Exists(direct))return direct;
            var native=Path.Combine(dir,"node_modules","@openai","codex","node_modules","@openai","codex-win32-x64","vendor","x86_64-pc-windows-msvc","bin","codex.exe");
            if(File.Exists(native))return native;
        }
        var fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm","node_modules","@openai","codex","node_modules","@openai","codex-win32-x64","vendor","x86_64-pc-windows-msvc","bin","codex.exe");
        return File.Exists(fallback)?fallback:null;
    }
    public async Task<ProviderSnapshot> RefreshAsync(bool force,CancellationToken cancellationToken)
    {
        var exe=string.IsNullOrWhiteSpace(settings.CodexExecutable)?DiscoverExecutable():settings.CodexExecutable;
        if(exe is null || !File.Exists(exe))throw new ProviderException("找不到 Codex 原生 codex.exe，请在设置中选择其完整路径。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30)); var ct=timeout.Token;
        // CLI startup was observed alongside config changes. Protect originals during the short
        // query instead of rewriting/restoring them. Expired auth that requires a write is unavailable.
        var home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        CodexReadGuard guard;try{guard=new CodexReadGuard(home);}catch(IOException){throw new ProviderException("Codex 配置或认证正在写入；稍后刷新。监控器不会覆盖 Harness 文件。");}
        using var protectedFiles=guard;
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("app-server");info.ArgumentList.Add("--stdio");info.ArgumentList.Add("-c");info.ArgumentList.Add("analytics.enabled=false");
        using var process=new Process {StartInfo=info};
        process.Start();
        // Drain diagnostics without retaining/outputting potentially sensitive text.
        var drain=Task.Run(async()=>{while(await process.StandardError.ReadLineAsync() is not null){} });
        try {
            await SendAsync(process,new {method="initialize",id=1,@params=new {clientInfo=new {name="quota_monitor",title="Quota Monitor",version="0.1.0"}}},ct);
            await ReceiveAsync(process,1,ct);
            await SendAsync(process,new {method="initialized",@params=new {}},ct);
            await SendAsync(process,new {method="account/rateLimits/read",id=2},ct);
            var response=await ReceiveAsync(process,2,ct);
            return Parse(response,DateTimeOffset.UtcNow);
        } finally {
            try { process.StandardInput.Close(); if(!process.HasExited) {using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(2));try {await process.WaitForExitAsync(stop.Token);}catch(OperationCanceledException){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}await drain; }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
    }
    private static async Task SendAsync(Process p,object message,CancellationToken ct){await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(),ct);await p.StandardInput.FlushAsync(ct);}
    private static async Task<JsonElement> ReceiveAsync(Process p,int id,CancellationToken ct)
    {
        while(await p.StandardOutput.ReadLineAsync(ct) is {} line){JsonDocument doc;try{doc=JsonDocument.Parse(line);}catch(JsonException){continue;}using(doc){var root=doc.RootElement;if(root.Count("id")!=id)continue;if(root.Field("error") is not null)throw new ProviderException("Codex app-server 拒绝额度读取（请检查 CLI 认证、版本和联网状态；原始错误已隐藏）。");return root.Field("result")?.Clone()??throw new ProviderException("Codex 响应缺少 result。");}}
        throw new ProviderException("Codex app-server 在响应前退出。");
    }
    public static ProviderSnapshot Parse(JsonElement response,DateTimeOffset now)
    {
        var windows=new List<QuotaWindow>(); string? selected=null;
        if(response.Field("rateLimitsByLimitId") is {ValueKind:JsonValueKind.Object} buckets && buckets.EnumerateObject().Any()) {
            foreach(var bucket in buckets.EnumerateObject())ReadBucket(bucket.Value,bucket.Name,windows);
            selected=buckets.TryGetProperty("codex",out _)?"codex":buckets.EnumerateObject().First().Name;
        } else if(response.Field("rateLimits") is {} single) {selected=single.Text("limitId")??"codex";ReadBucket(single,selected,windows);}
        var primary=windows.FirstOrDefault(w=>w.Bucket==selected&&w.Name=="Primary")??windows.FirstOrDefault(w=>w.Bucket==selected);
        return ProviderSnapshot.Empty("codex","Codex",MetricType.Quota) with {ConnectionStatus=windows.Count>0?ConnectionStatus.Ready:ConnectionStatus.Unavailable,Windows=windows,RemainingPercent=primary?.RemainingPercent,ResetAt=primary?.ResetAt,LastUpdated=now,DataSource="Codex app-server · account/rateLimits/read",Note="显示服务端真实窗口，原始 usedPercent 保留。",ErrorMessage=windows.Count==0?"服务端未返回可用配额窗口；可能不是订阅认证。":null};
    }
    private static void ReadBucket(JsonElement bucket,string id,List<QuotaWindow> windows)
    {
        foreach(var (field,label) in new[]{("primary","Primary"),("secondary","Secondary")})
            if(bucket.Field(field) is {} w && w.Number("usedPercent") is {} used)
                windows.Add(new(id,label,(double)used,w.Count("windowDurationMins"),JsonFields.Unix(w.Count("resetsAt"))));
    }
}
