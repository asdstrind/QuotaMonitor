using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;

var settings=new MonitorSettings();
if(args.Contains("--dsh-watch")){
    // Transient console observation only. Do not persist account balances in the workspace.
    await using var receiver=new QuotaMonitor.Core.Services.DshBalanceReceiver();
    var seconds=int.TryParse(args.SkipWhile(x=>x!="--seconds").Skip(1).FirstOrDefault(),out var duration)?Math.Clamp(duration,2,1800):600;
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    string? last=null;
    try{while(!stop.IsCancellationRequested){
        var reading=await receiver.ReadAsync(stop.Token);
        var key=$"{reading.At(DateTimeOffset.UtcNow)} {reading.Balance?.ObservedAt:O}";
        if(key!=last){Console.WriteLine($"{DateTimeOffset.UtcNow:O} {key}");if(args.Contains("--show-balance")&&reading.Balance is {} b)Console.WriteLine($"Transient display: recharge {DshBalancePresentation.Normal(b)}; bonus {string.Join(" + ",b.BonusWallets.Select(DshBalancePresentation.Wallet))}");last=key;}
        await Task.Delay(500,stop.Token);
    }}catch(OperationCanceledException){}
    return;
}
if(args.Contains("--dsh-balance")){
    await using var receiver=new QuotaMonitor.Core.Services.DshBalanceReceiver();
    var seconds=int.TryParse(args.SkipWhile(x=>x!="--seconds").Skip(1).FirstOrDefault(),out var duration)?Math.Clamp(duration,2,120):45;
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    DshBalanceReading result=DshBalanceReading.Empty;
    try{while(!stop.IsCancellationRequested){result=await receiver.ReadAsync(stop.Token);if(result.Balance is not null)break;await Task.Delay(250,stop.Token);}}catch(OperationCanceledException){}
    var output=Path.GetFullPath(args.SkipWhile(x=>x!="--output").Skip(1).FirstOrDefault()??"evidence/dsh-balance-adapter-live.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(result.Balance is null?"DSH Balance Bridge: unavailable (no official snapshot received).":"DSH Balance Bridge: "+result.At(DateTimeOffset.UtcNow)+" (actual official UI wallet snapshot received). See "+output);
    return;
}
if(args.Contains("--dsh-sessions")){
    // Per-session parser outcome (counts and dates only) to audit which usage rows are excluded.
    foreach(var dir in Directory.EnumerateDirectories(settings.DshSessionRoot,"*",SearchOption.AllDirectories).OrderBy(d=>d,StringComparer.Ordinal)){
        var names=Directory.EnumerateFiles(dir,"session*.jsonl*").Select(Path.GetFileName).Where(n=>System.Text.RegularExpressions.Regex.IsMatch(n!,@"^session(?:\.v\d+)?\.jsonl(?:\.zstd)?$")).ToArray();
        if(names.Length==0)continue;
        foreach(var name in names.OrderByDescending(n=>DshAdapter.Generation(n!))){
            var file=Path.Combine(dir,name!);
            try{var s=DshAdapter.ReadSession(file,CancellationToken.None);var days=s.Usage.GroupBy(u=>u.At.LocalDateTime.ToString("MM-dd")).Select(g=>$"{g.Key}:{g.Count()}");
                Console.WriteLine($"{Path.GetFileName(dir)[..Math.Min(14,Path.GetFileName(dir).Length)],-14} {name,-24} usage {s.Usage.Count,4} invalid {s.InvalidRows} torn {s.TornTail} days [{string.Join(" ",days)}]");}
            catch(Exception ex){Console.WriteLine($"{Path.GetFileName(dir)[..Math.Min(14,Path.GetFileName(dir).Length)],-14} {name,-24} ERROR {ex.GetType().Name}: {ex.Message}");}
        }
    }
    return;
}
if(args.Contains("--antigravity-history")){
    // Only daily aggregates; no account reads, quota requests, conversation IDs or bodies.
    var today=DateOnly.FromDateTime(DateTime.Now);
    var result=await new QuotaMonitor.Core.Services.AntigravityHistorySource().ReadHistoryAsync(null,new(today,UsageGrid.WindowStart(today,UsageGrid.DefaultWeeks),true),CancellationToken.None);
    var output=Path.GetFullPath(args.SkipWhile(x=>x!="--output").Skip(1).FirstOrDefault()??"evidence/ui-v1.6.2/local-history.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"Antigravity: {result.Days.Count} days; {result.Days.Sum(d=>d.Tokens??0):N0} confirmed tokens; {result.Error??"Complete local history"}");return;
}
if(args.Contains("--history")){
    // Local daily token history only (Codex rollouts, DSH sessions). Aggregates; no message bodies.
    var today=DateOnly.FromDateTime(DateTime.Now);var start=UsageGrid.WindowStart(today,UsageGrid.DefaultWeeks);var request=new HistoryRequest(today,start,true);
    var watch=System.Diagnostics.Stopwatch.StartNew();
    var codex=await new QuotaMonitor.Core.Services.CodexHistorySource().ReadHistoryAsync(null,request,CancellationToken.None);var codexMs=watch.ElapsedMilliseconds;watch.Restart();
    var again=await new QuotaMonitor.Core.Services.CodexHistorySource().ReadHistoryAsync(null,request,CancellationToken.None);
    var dsh=await new DshAdapter(settings).ReadHistoryAsync(null,request,CancellationToken.None);var dshMs=watch.ElapsedMilliseconds;
    foreach(var h in new[]{codex,dsh}){
        var active=h.Days.Where(d=>d.Tokens>0).ToArray();
        Console.WriteLine($"{h.ProviderId}: coverage {h.CoverageStart} · {h.Days.Count} covered days · {active.Length} active · tokens {h.Days.Sum(d=>d.Tokens??0):N0} · requests {h.Days.Sum(d=>d.Requests??0):N0} · estimated {h.Days.Count(d=>d.Quality==UsageQuality.Estimated)} · partial {h.Days.Count(d=>d.Quality==UsageQuality.Partial)} · p95 {UsageGrid.Percentile95(active.Select(d=>d.Tokens!.Value).ToArray()):N0}");
        if(h.Error is not null)Console.WriteLine("  error: "+h.Error);if(h.Note is not null)Console.WriteLine("  note: "+h.Note);
    }
    Console.WriteLine($"codex scan {codexMs} ms; repeated scan identical: {codex.Days.SequenceEqual(again.Days)}; dsh scan {dshMs} ms");
    var grid=UsageGrid.Build([codex,dsh],today);
    Console.WriteLine($"grid {grid.Start}..{grid.Today} · {grid.Days.Count} days · months {string.Join(",",grid.Months.Select(m=>m.Column+":"+m.Month.Month))}");
    var output=Path.GetFullPath(args.SkipWhile(x=>x!="--output").Skip(1).FirstOrDefault()??"evidence/history-local.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Codex=codex,Dsh=dsh},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("written "+output);
    return;
}
if(args.Contains("--live")){
    var results=new List<ProviderSnapshot>();
    foreach(IProviderAdapter adapter in new IProviderAdapter[]{new CodexAdapter(settings),new DshAdapter(settings)}){
        try{using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(2));results.Add(await adapter.RefreshAsync(true,stop.Token));}
        catch(Exception ex){results.Add(ProviderSnapshot.Empty(adapter.Id,adapter.Id,MetricType.Tokens) with{ConnectionStatus=ConnectionStatus.Unavailable,ErrorMessage=ex is ProviderException?ex.Message:ex.GetType().Name});}
    }
    var json=JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true});
    var output=Path.GetFullPath(args.SkipWhile(x=>x!="--output").Skip(1).FirstOrDefault()??"evidence/live-adapters.json");Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,json);Console.WriteLine(json);
}else Console.WriteLine("Use --live [--output path], or --dsh-balance [--seconds 45] [--output path]. No credentials or message bodies are printed.");
