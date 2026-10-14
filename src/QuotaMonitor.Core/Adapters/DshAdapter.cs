using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using ZstdSharp;

namespace QuotaMonitor.Core.Adapters;
public sealed record DshUsage(long Input, long Output, long CacheRead, long CacheWrite, DateTimeOffset At, string Model)
{
    public long Total => checked(Input+Output+CacheRead+CacheWrite);
}
public sealed record DshSession(string Id,IReadOnlyList<DshUsage> Usage,int InvalidRows,bool TornTail,IReadOnlyList<DateTimeOffset>? InvalidAt=null);
public sealed class DshAdapter(MonitorSettings settings, IDshBalanceSource? balanceSource=null) : IProviderAdapter, IUsageHistorySource
{
    public string Id=>"dsh";
    private readonly Dictionary<string,(long Size,DateTime Modified,DshSession Session)> cache=new(StringComparer.OrdinalIgnoreCase);
    private sealed record ScannedSession(DateTime Modified,DshSession Session);
    private sealed record Scan(IReadOnlyList<ScannedSession> Sessions,int Selected,IReadOnlyList<DateTime> FailedModified);
    public async Task<ProviderSnapshot> RefreshAsync(bool force,CancellationToken ct)
    {
        var balanceTask=balanceSource?.ReadAsync(ct)??Task.FromResult(DshBalanceReading.Empty);
        ProviderSnapshot usage;
        try{usage=await Task.Run(()=>Read(ct),ct);}
        catch(ProviderException ex){usage=ProviderSnapshot.Empty(Id,"DSH",MetricType.Balance) with{ConnectionStatus=ConnectionStatus.Unavailable,ErrorMessage=ex.Message};}
        var balance=await balanceTask;
        return usage with {MetricType=MetricType.Balance,AccountBalance=balance,ConnectionStatus=balance.At(DateTimeOffset.UtcNow)==DshBalanceState.Ready&&usage.ConnectionStatus==ConnectionStatus.Unavailable?ConnectionStatus.Partial:usage.ConnectionStatus,DataSource=usage.DataSource+" · 官方 Host getBalance / Named Pipe",Note="复用 DSH 官方账号服务；充值与赠送钱包分别保留原始币种和金额。Host 启动后立即查询，成功后每 3 分钟后台刷新；失败保留最近余额并标记 Stale。 本地用量仅覆盖保留的 Session。"};
    }
    private ProviderSnapshot Read(CancellationToken ct)
    {
        var scan=ScanSessions(ct);
        var selected=scan.Selected;var failures=scan.FailedModified.Count;
        var sessions=scan.Sessions.Select(s=>s.Session).GroupBy(s=>s.Id).Select(g=>g.First()).ToArray();
        var usage=sessions.SelectMany(s=>s.Usage).ToArray();
        if(usage.Length==0) return ProviderSnapshot.Empty(Id,"DSH",MetricType.Tokens) with {ConnectionStatus=ConnectionStatus.Unavailable,DataSource="只读 DSH Session v0–v4",ErrorMessage=$"未找到可验证的请求用量（选取 {selected} 个 Session，{failures} 个读取失败）。"};
        var today=DateTime.Today;var todays=usage.Where(x=>x.At.LocalDateTime.Date==today).ToArray();
        var latest=usage.MaxBy(x=>x.At)!;
        var estimated=Estimate(todays,settings);
        var errors=failures+sessions.Sum(s=>s.InvalidRows)+(sessions.Any(s=>s.TornTail)?1:0);
        return ProviderSnapshot.Empty(Id,"DSH",MetricType.Tokens) with {ConnectionStatus=errors>0?ConnectionStatus.Partial:ConnectionStatus.Ready,TodayTokens=todays.Sum(x=>x.Total),TotalTokens=usage.Sum(x=>x.Total),InputTokens=todays.Sum(x=>checked(x.Input+x.CacheRead+x.CacheWrite)),OutputTokens=todays.Sum(x=>x.Output),TodayRequests=todays.LongLength,TodayCost=estimated,Currency=settings.DshPriceCurrency,IsEstimated=estimated.HasValue,CurrentModel=latest.Model,LastUpdated=DateTimeOffset.UtcNow,UsageUpdated=latest.At,DataSource=$"DSH 本地服务端 usage · {sessions.Length} 个 Session · {usage.Length} 次结算",Note="余额 Unavailable · 本地累计仅覆盖保留的 Session（含子 Agent），不代表账户全历史。"+(estimated.HasValue?" Estimated 价格依据："+settings.DshPriceBasis:" 费用 Unavailable：估算已关闭，或模型 / 价格依据不匹配；不影响余额与 Token 监控。"),ErrorMessage=errors>0?$"{errors} 项缺失、损坏或未完成记录已排除；统计可能不完整。":null};
    }
    // One locked scan feeds both the live snapshot and the daily history, so the session cache is
    // never mutated concurrently and unchanged files are parsed only once.
    private Scan ScanSessions(CancellationToken ct)
    {
        if(!Directory.Exists(settings.EffectiveDshSessionRoot))throw new ProviderException("尚未发现 DSH Session；运行 DSH 后会自动读取。自定义目录可在高级设置中指定。");
        lock(cache){
            var all=Directory.EnumerateFiles(settings.EffectiveDshSessionRoot,"session*.jsonl*",SearchOption.AllDirectories).Where(p=>Regex.IsMatch(Path.GetFileName(p),@"^session(?:\.v\d+)?\.jsonl(?:\.zstd)?$")).ToArray();
            var selected=all.GroupBy(Path.GetDirectoryName).Select(g=>g.OrderByDescending(Generation).ThenBy(p=>p,StringComparer.Ordinal).First()).ToArray();
            var results=new List<ScannedSession>();var failed=new List<DateTime>();
            foreach(var file in selected) {
                ct.ThrowIfCancellationRequested();
                try {
                    var info=new FileInfo(file);
                    var initialSize=info.Length;var initialModified=info.LastWriteTimeUtc;
                    if(!cache.TryGetValue(file,out var cached)||cached.Size!=info.Length||cached.Modified!=info.LastWriteTimeUtc){
                        var session=ReadSession(file,ct);
                        // Do not cache a changing file or a torn tail; retry next refresh.
                        info.Refresh(); if(!session.TornTail&&info.Length==initialSize&&info.LastWriteTimeUtc==initialModified)cache[file]=(info.Length,info.LastWriteTimeUtc,session);
                        results.Add(new(initialModified,session));
                    } else results.Add(new(cached.Modified,cached.Session));
                }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){failed.Add(SafeModified(file));}
            }
            foreach(var key in cache.Keys.Except(selected,StringComparer.OrdinalIgnoreCase).ToArray())cache.Remove(key);
            return new(results,selected.Length,failed);
        }
    }
    private static DateTime SafeModified(string file){try{return File.GetLastWriteTimeUtc(file);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return DateTime.MinValue;}}
    public Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous,HistoryRequest request,CancellationToken ct)=>Task.Run(()=>{
        try{return ReadHistory(request.Today,TimeZoneInfo.Local,ct);}
        catch(ProviderException ex){return UsageHistory.Empty(Id) with{Error=ex.Message,Source=HistorySource,Scope=HistoryScope,UpdatedAt=DateTimeOffset.UtcNow};}
    },ct);
    private const string HistorySource="DSH 本机 Session · 服务端 usage（DeepSeek 路由）";
    private const string HistoryScope="本机保留的 DSH Session（含子 Agent）";
    public UsageHistory ReadHistory(DateOnly today,TimeZoneInfo zone,CancellationToken ct)
    {
        var scan=ScanSessions(ct);
        DateOnly Local(DateTimeOffset at)=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at,zone).DateTime);
        DateOnly? LocalFile(DateTime utc)=>utc==DateTime.MinValue?null:Local(new DateTimeOffset(DateTime.SpecifyKind(utc,DateTimeKind.Utc)));
        var unique=scan.Sessions.GroupBy(s=>s.Session.Id).Select(g=>g.First()).ToArray();
        var partial=new Dictionary<DateOnly,int>();int unattributed=0;
        void Flag(DateOnly day)=>partial[day]=partial.GetValueOrDefault(day)+1;
        foreach(var s in unique){
            var dated=s.Session.InvalidAt??[];
            foreach(var at in dated)Flag(Local(at));
            unattributed+=Math.Max(0,s.Session.InvalidRows-dated.Count);
            // A torn tail is the frame still being written: the file's own write day is incomplete.
            if(s.Session.TornTail){if(LocalFile(s.Modified) is {} d)Flag(d);else unattributed++;}
        }
        foreach(var m in scan.FailedModified){if(LocalFile(m) is {} d)Flag(d);else unattributed++;}
        var byDay=unique.SelectMany(s=>s.Session.Usage).GroupBy(u=>Local(u.At)).ToDictionary(g=>g.Key,g=>g.ToArray());
        if(byDay.Count==0&&partial.Count==0)return UsageHistory.Empty(Id) with{Source=HistorySource,Scope=HistoryScope,UpdatedAt=DateTimeOffset.UtcNow,Error=$"未找到可验证的请求用量（{scan.Selected} 个 Session）。"};
        var start=byDay.Keys.Concat(partial.Keys).Min();
        var days=new List<UsageDay>();
        for(var day=start;day<=today;day=day.AddDays(1)){
            var excluded=partial.GetValueOrDefault(day);var quality=excluded>0?UsageQuality.Partial:UsageQuality.Exact;
            if(byDay.TryGetValue(day,out var rows))days.Add(new(day,rows.Sum(x=>x.Total),rows.LongLength,Estimate(rows,settings),quality,null,rows.Sum(x=>x.CacheRead),excluded));
            else days.Add(new(day,0,0,0m,quality,null,0,excluded));
        }
        var notes=new List<string>{"费用为按设置单价估算，非账户账单；混用未定价模型的日期不估算。"};
        if(unattributed>0)notes.Add($"另有 {unattributed} 项无法确定日期的缺失或损坏记录已排除。");
        return new UsageHistory{ProviderId=Id,Days=days,CoverageStart=start,Source=HistorySource,Scope=HistoryScope,Note=string.Join(" ",notes),CostCurrency=settings.DshPriceCurrency,CostEstimated=true,UpdatedAt=DateTimeOffset.UtcNow};
    }
    public static decimal? Estimate(IReadOnlyList<DshUsage> usage,MonitorSettings s)
    {
        if(!s.DshEstimateEnabled||string.IsNullOrWhiteSpace(s.DshPricedModel)||string.IsNullOrWhiteSpace(s.DshPriceBasis)||s.DshInputPerMillion is null||s.DshCacheReadPerMillion is null||s.DshOutputPerMillion is null)return null;
        if(s.DshCacheWritePerMillion is null&&usage.Any(x=>x.CacheWrite!=0))return null;
        if(usage.Any(x=>x.Model!=s.DshPricedModel))return null;
        return usage.Sum(x=>(x.Input*s.DshInputPerMillion.Value+x.Output*s.DshOutputPerMillion.Value+x.CacheRead*s.DshCacheReadPerMillion.Value+x.CacheWrite*(s.DshCacheWritePerMillion??0))/1000000m);
    }
    public static int Generation(string file){var m=Regex.Match(Path.GetFileName(file),@"\.v(\d+)\.");return m.Success?int.Parse(m.Groups[1].Value):0;}
    public static DshSession ReadSession(string file,CancellationToken ct)
    {
        if(Generation(file)>4)throw new InvalidDataException("Unsupported session version");
        using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(input.Length>256*1024*1024)throw new InvalidDataException("Session size exceeds bounded reader");
        using var memory=new MemoryStream();input.CopyTo(memory);ct.ThrowIfCancellationRequested();
        var data=memory.ToArray();var text=new StringBuilder();bool torn=false;
        if(file.EndsWith(".zstd",StringComparison.OrdinalIgnoreCase)) {
            int offset=0;
            while(offset<data.Length){ct.ThrowIfCancellationRequested();int length=FrameLength(data.AsSpan(offset));if(length==0){torn=true;break;}
                try {using var compressed=new MemoryStream(data,offset,length,false);using var decoded=new DecompressionStream(compressed);using var reader=new StreamReader(decoded,Encoding.UTF8);char[] buffer=new char[32768];int n;int total=0;while((n=reader.Read(buffer))>0){ct.ThrowIfCancellationRequested();total+=n;if(total>64*1024*1024)throw new InvalidDataException("Frame too large");text.Append(buffer,0,n);}}
                catch(ZstdException){throw new InvalidDataException("Compressed frame rejected");}offset+=length;
            }
        }else text.Append(Encoding.UTF8.GetString(data));
        return ParseLines(text.ToString(),torn,ct);
    }
    // Zstd frame framing (RFC 8878); complete frames are checksum-validated by ZstdSharp.
    public static int FrameLength(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length<5)return 0;
        if(BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0xFD2FB528)throw new InvalidDataException("Invalid zstd magic");
        var descriptor=bytes[4];if((descriptor&0x18)!=0)throw new InvalidDataException("Reserved zstd flag");
        bool single=(descriptor&0x20)!=0;int flag=descriptor>>6;
        int pos=5+(single?0:1)+new[]{0,1,2,4}[descriptor&3]+(flag==0?(single?1:0):new[]{0,2,4,8}[flag]);
        if(pos>bytes.Length)return 0;
        while(true){if(pos+3>bytes.Length)return 0;var block=(uint)(bytes[pos]|bytes[pos+1]<<8|bytes[pos+2]<<16);pos+=3;int type=(int)((block>>1)&3);if(type==3)throw new InvalidDataException("Reserved zstd block");int size=type==1?1:(int)(block>>3);pos+=size;if(pos>bytes.Length)return 0;if((block&1)!=0)break;}
        if((descriptor&4)!=0)pos+=4;return pos<=bytes.Length?pos:0;
    }
    public static DshSession ParseLines(string text,bool torn=false,CancellationToken ct=default)
    {
        var rows=new List<(int Index,JsonElement Event)>();int invalid=0;string? id=null;int version=0;long? seedLength=null;bool seeded=false;int physicalIndex=0;
        using var lines=new StringReader(text);string? line;
        while((line=lines.ReadLine()) is not null){ct.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(line))continue;try{using var doc=JsonDocument.Parse(line);var e=doc.RootElement;var type=e.Text("type");
            if(type=="session"){id=e.Text("id");version=(int)(e.Count("version")??0);seedLength=e.Count("seedLength");seeded=e.Field("isSeeded")?.ValueKind==JsonValueKind.True;continue;}
            var index=physicalIndex++;
            if(type is not("request/context" or "model/selection" or "session/end-seed" or "llm/retry-started" or "assistant/message" or "assistant/attempt"))continue;
            var d=e.Field("data");
            // Retain only route, timing, and numeric usage; release all prompt/tool content immediately.
            var projected=JsonSerializer.SerializeToElement(new {type,seq=e.Count("seq"),time=e.Count("time"),data=new {turn=d?.Count("turn"),step=d?.Count("step"),model=d?.Text("model"),provider=d?.Text("provider"),inherited=d?.Field("inherited")?.ValueKind==JsonValueKind.True,usage=d?.Field("usage")??FindStreamUsage(d?.Field("stream")),message=new {source=d?.Field("message")?.Field("source")}}});
            rows.Add((index,projected));
        }catch(JsonException){invalid++;physicalIndex++;}}
        if(id is null||version>4)throw new InvalidDataException("Missing or unsupported session header");
        // Installed 0.2.0-rc.2 semantics: v0/v1 are seeded only when the header has seedLength, and their
        // inherited marker sits at seq==seedLength; v2+ inherit through the last end-seed tagged
        // inherited:true. An untagged end-seed only separates a reopened log from its new events, so it
        // never removes the session's own earlier usage.
        bool IsSeedMarker(JsonElement e)=>e.Text("type")=="session/end-seed";
        int cut=0;
        if(version<2){
            if(seedLength is {} inheritedCount){
                var marker=rows.FindIndex(e=>IsSeedMarker(e.Event)&&e.Event.Count("seq")==inheritedCount);
                // Without its boundary marker the historical row count cannot be verified.
                if(marker<0)return new(id,[],invalid+1,torn);
                cut=rows[marker].Index+1;
            }
        }else{
            var marker=rows.FindLastIndex(e=>IsSeedMarker(e.Event)&&e.Event.Field("data")?.Field("inherited")?.ValueKind==JsonValueKind.True);
            if(seeded!=(marker>=0))return new(id,[],invalid+1,torn);
            if(marker>=0)cut=rows[marker].Index+1;
        }
        var records=new Dictionary<(long Turn,long Step,int Attempt),DshUsage>();var retry=new Dictionary<(long,long),int>();string model="未知模型";string? provider=null;var invalidAt=new List<DateTimeOffset>();
        foreach(var (rowIndex,e) in rows) {
            ct.ThrowIfCancellationRequested();if(e.Field("data") is not {} d)continue;
            var type=e.Text("type");
            if(type=="request/context"||type=="model/selection"){model=d.Text("model")??model;provider=d.Text("provider")??provider;}
            if(rowIndex<cut)continue;
            var turn=d.Count("turn");var step=d.Count("step");if(turn is null||step is null)continue;
            var pair=(turn.Value,step.Value);
            if(type=="llm/retry-started"){retry[pair]=retry.GetValueOrDefault(pair)+1;continue;}
            if(type!="assistant/message"&&type!="assistant/attempt")continue;
            if(d.Field("message")?.Field("source") is {} source){model=source.Text("model")??model;provider=source.Text("provider")??provider;}
            if(provider is null||!provider.StartsWith("deepseek",StringComparison.OrdinalIgnoreCase))continue;
            var usage=d.Field("usage")??FindStreamUsage(d.Field("stream"));
            var rowTime=e.Count("time") is {} rt&&rt<=253402300799999L?DateTimeOffset.FromUnixTimeMilliseconds(rt):(DateTimeOffset?)null;
            void Reject(){invalid++;if(rowTime is {} t)invalidAt.Add(t);}
            if(usage is not {} u){Reject();continue;}
            var input=u.Count("inputTokens");var output=u.Count("outputTokens");
            if(input is null||output is null||!ValidOptional(u,"cacheReadTokens")||!ValidOptional(u,"cacheWriteTokens")||!ValidOptional(u,"reasoningTokens")){Reject();continue;}
            var cached=u.Count("cacheReadTokens")??0;var write=u.Count("cacheWriteTokens")??0;
            long total;try{total=checked(input.Value+output.Value+cached+write);}catch(OverflowException){Reject();continue;}
            if((u.Count("totalTokens") is {} declared&&declared!=total)||(u.Count("reasoningTokens") is {} reasoning&&reasoning>output)){Reject();continue;}
            if(e.Count("time") is not {} stamp){invalid++;continue;}
            DateTimeOffset at;try{at=DateTimeOffset.FromUnixTimeMilliseconds(stamp);}catch(ArgumentOutOfRangeException){invalid++;continue;}
            records[(pair.Item1,pair.Item2,retry.GetValueOrDefault(pair))]=new(input.Value,output.Value,cached,write,at,model);
        }
        return new(id,records.Values.ToArray(),invalid,torn,invalidAt);
    }
    private static bool ValidOptional(JsonElement e,string field)=>e.Field(field) is null||e.Count(field) is not null;
    private static JsonElement? FindStreamUsage(JsonElement? stream)
    {
        if(stream is null)return null;
        var chunks=stream.Value.ValueKind==JsonValueKind.Array?stream:stream.Value.Field("chunks");
        if(chunks is not {ValueKind:JsonValueKind.Array} a)return null;
        JsonElement? found=null;foreach(var c in a.EnumerateArray())if(c.Text("type")=="usage")found=c.Field("usage");return found;
    }
}

