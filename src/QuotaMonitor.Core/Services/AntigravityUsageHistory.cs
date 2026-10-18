using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaMonitor.Core.Models;

namespace QuotaMonitor.Core.Services;

/// <summary>Local metadata only. Independent of quota pools, accounts, credentials and network.</summary>
public sealed class AntigravityHistorySource(string? geminiHome = null, string? tokscaleHome = null, TimeZoneInfo? zone = null) : IUsageHistorySource
{
    public string Id => "antigravity";
    public Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous, HistoryRequest request, CancellationToken ct) =>
        Task.Run(() =>
        {
            var current=Read(request,ct);
            if(current.Error is not null && previous is {Days.Count: >0} && previous.Days.Any(d=>d.Tokens>0) &&
                (previous.Error is null || previous.Error.StartsWith("Partial · Stale",StringComparison.Ordinal)) && previous.Source==current.Source && previous.Scope==current.Scope)
                return previous with{Days=previous.Days.Where(d=>d.Tokens>0).Select(d=>d with{Quality=UsageQuality.Partial}).ToArray(),Error="Partial · Stale · 保留上次完整本地统计；本次历史读取不完整。",Note=previous.Note?.Split(" 上次成功读取：")[0]+" 上次成功读取："+previous.UpdatedAt?.ToString("O")+"；当前状态："+current.Error};
            return current;
        }, ct);

    public UsageHistory Read(HistoryRequest request, CancellationToken ct = default)
    {
        var home = geminiHome ?? Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        var cache = tokscaleHome ?? Environment.GetEnvironmentVariable("TOKSCALE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "tokscale");
        var timezone = zone ?? TimeZoneInfo.Local;
        var budget = new Budget(ct);
        var events = new Dictionary<string, Event>();
        var aliases = new Dictionary<string, string>(); var conflicts = new HashSet<string>();
        var partial = false; var recognized = false; var files = new List<string>(); var excluded = 0;
        string source = "Antigravity local SQLite · gen_metadata / steps.metadata";
        var scope = "本机 Antigravity 已保存历史 · 非账户/额度池统计 · " + timezone.Id+" · roots:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home).ToUpperInvariant()+"|"+Path.GetFullPath(cache).ToUpperInvariant())))[..12];
        UsageHistory Empty(string error) => new() { ProviderId=Id, Source=source, Scope=scope, Error=error, UpdatedAt=DateTimeOffset.UtcNow };
        void Add(Event value, params string[] keys)
        {
            // Request IDs deduplicate across conversations; row identity also catches edited/copied DBs.
            var identities=keys.Where(k=>k.Length>0).Distinct().ToArray();
            var groups=identities.Where(aliases.ContainsKey).Select(k=>aliases[k]).Distinct().ToArray();
            var key=groups.FirstOrDefault() ?? identities[0];
            foreach(var other in groups.Skip(1))
            {
                if(events.TryGetValue(other,out var old) && old!=value) conflicts.Add(key);
                if(conflicts.Contains(other)) conflicts.Add(key);
                events.Remove(other);
                foreach(var alias in aliases.Where(p=>p.Value==other).Select(p=>p.Key).ToArray()) aliases[alias]=key;
            }
            if(events.TryGetValue(key,out var existing) && existing!=value) conflicts.Add(key);
            events.TryAdd(key,value);
            foreach(var id in identities) aliases[id]=key;
        }
        try
        {
            foreach(var root in new[]{Path.Combine(home,"antigravity-cli","conversations"),Path.Combine(home,"antigravity"),Path.Combine(home,"antigravity","conversations")})
            {
                budget.Check();
                try
                {
                    FileAttributes attributes;
                    try{attributes=File.GetAttributes(root);}
                    catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){continue;}
                    if((attributes&FileAttributes.Directory)==0) { partial=true;continue; }
                    foreach(var file in Directory.EnumerateFiles(root,"*.db",SearchOption.TopDirectoryOnly))
                    { budget.Check(); files.Add(Path.GetFullPath(file)); if(files.Count>500)throw new BudgetExceeded(); }
                }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException) { partial=true; }
            }
            files=files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if(files.Count>0)
            {
                foreach(var file in files)
                {
                    budget.Check();
                    try
                    {
                        var attempt=ReadDatabase(file,budget);
                        recognized|=attempt.Recognized; partial|=attempt.Partial; excluded+=attempt.Excluded;
                        foreach(var (idx,ev,response,identity) in attempt.Events)
                            Add(ev,"row:"+Path.GetFileNameWithoutExtension(file)+":"+idx,response is null?identity??"":"request:"+response);
                    }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
                    { partial=true;excluded++; }
                }
            }
            else if(!partial)
            {
                source="Tokscale Antigravity normalized JSONL cache (existing files only)";
                var root=Path.Combine(cache,"antigravity-cache","sessions");
                if(Directory.Exists(root))
                {
                    foreach(var file in Directory.EnumerateFiles(root,"*.jsonl",SearchOption.TopDirectoryOnly))
                    {
                        budget.Check(); if(++budget.Files>500)throw new BudgetExceeded();
                        try
                        {
                            var info=Stamp(file); if(new FileInfo(file).Length>16*1024*1024)throw new BudgetExceeded();
                            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                            using var reader=new StreamReader(stream,new UTF8Encoding(false,true));
                            string? session=null,model=null;var line=0;
                            // Normalized cache is a subset of local history: completeness is never asserted.
                            partial=true;
                            while(ReadLine(reader,budget) is {} json)
                            {
                                line++;if(string.IsNullOrWhiteSpace(json))continue;
                                try
                                {
                                    using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=12});var r=doc.RootElement;
                                    if(r.GetProperty("type").GetString()=="session_meta")
                                    {
                                        var id=JText(r,"sessionId");if(id is null||session is not null&&session!=id)throw new InvalidDataException();
                                        session=id; model=JText(r,"modelId","model_id");recognized=true;continue;
                                    }
                                    if(r.GetProperty("type").GetString()!="usage")throw new InvalidDataException();
                                    var sid=JText(r,"sessionId")??session;
                                    if(sid is null || session is not null&&sid!=session)throw new InvalidDataException();
                                    session=sid;recognized=true;
                                    var input=JCounter(r,true,"input");var output=JCounter(r,true,"output");
                                    var cached=JCounter(r,false,"cacheRead","cache_read");var write=JCounter(r,false,"cacheWrite","cache_write");
                                    if(JCounter(r,false,"reasoning")!=0)throw new InvalidDataException(); // Overlap not established by cache producer.
                                    var at=DateTimeOffset.FromUnixTimeMilliseconds(JCounter(r,true,"timestamp"));ValidateTime(at);
                                    var ev=new Event(at,JText(r,"modelId","model_id")??model??"unknown",input,write,cached,output,0);
                                    _=ev.Total;var response=JText(r,"responseId","response_id");
                                    Add(ev,"json:"+sid+":"+line,response is null?"":"request:"+response);
                                }
                                catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or ArgumentException or OverflowException)
                                { partial=true;excluded++; }
                            }
                            if(info!=Stamp(file)){partial=true;excluded++;}
                        }
                        catch(Exception e) when(e is IOException or UnauthorizedAccessException or DecoderFallbackException) {partial=true;excluded++;}
                    }
                }
            }
            budget.Check();ct.ThrowIfCancellationRequested();
            if(conflicts.Count>0){partial=true;excluded+=conflicts.Count;}
            if(!recognized)return Empty(partial?"Unavailable · 本地历史未能完整识别/读取。":"Unavailable · 未发现可识别的本地 Token 历史。");
            var verified=events.Where(p=>!conflicts.Contains(p.Key)).Select(p=>p.Value).ToArray();
            var groups=verified.GroupBy(e=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.At,timezone).DateTime)).ToDictionary(g=>g.Key,g=>g.ToArray());
            var days=new List<UsageDay>();var now=DateTimeOffset.UtcNow;
            // Successful reads establish zeros only within dated local history coverage.
            // An empty schema cannot establish when history started.
            if(groups.Count==0)return Empty("Unavailable · 本地历史尚无可确定覆盖范围的时间记录。");
            var coverage=groups.Keys.Min()>request.WindowStart?groups.Keys.Min():request.WindowStart;
            var dates=partial?groups.Keys.Where(d=>d>=request.WindowStart&&d<=request.Today):Enumerable.Range(0,Math.Max(0,request.Today.DayNumber-coverage.DayNumber+1)).Select(i=>coverage.AddDays(i));
            foreach(var date in dates.Order())
            {
                var rows=groups.GetValueOrDefault(date)??[];
                var tokens=rows.Aggregate(0L,(sum,e)=>checked(sum+e.Total));
                // A partial zero does not prove any known usage, and must not appear as a true zero.
                if(partial&&tokens==0)continue;
                days.Add(new(date,tokens,rows.LongLength,null,partial?UsageQuality.Partial:UsageQuality.Exact,now,rows.Aggregate(0L,(sum,e)=>checked(sum+e.CacheRead)),partial?Math.Max(1,excluded):0));
            }
            if(days.Count==0&&partial)return Empty("Unavailable · 存在不完整历史，但没有可核验的窗口内 Token 数。");
            return new(){ProviderId=Id,Days=days,CoverageStart=days.Count>0?days.Min(d=>d.Date):null,UpdatedAt=now,Source=source,Scope=scope,
                Error=partial?"Partial · 本地历史不完整；仅包含已确认 Token。":null,
                Note=$"输入 + 缓存写入 + 缓存读取 + 输出（含 Thinking，计一次）。扫描 {files.Count} 个 SQLite；{verified.Length} 条去重记录；{excluded} 处记录或附加结构未能完整核验（整个扫描范围，不是各日期的排除条数）。完整仅指本机现存可识别历史，不代表云端或已删除会话完整。"};
        }
        catch(BudgetExceeded) { return Empty("Unavailable · 历史扫描达到时间/文件/行/字节上限；未发布截断统计。"); }
        catch(OverflowException) { return Empty("Unavailable · Token 汇总溢出，未发布数字。"); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) {return Empty("Unavailable · 本机历史或 SQLite 运行时不可读。");}
    }

    private sealed class BudgetExceeded : Exception;
    private sealed class Budget(CancellationToken ct)
    {
        private readonly Stopwatch clock=Stopwatch.StartNew();private long bytes;private int rows;public int Files;
        public bool Stop => ct.IsCancellationRequested||clock.Elapsed>TimeSpan.FromSeconds(10);
        public void Check(){ct.ThrowIfCancellationRequested();if(Stop)throw new BudgetExceeded();}
        public void AddBytes(int count){Check();bytes+=count;if(bytes>64*1024*1024)throw new BudgetExceeded();}
        public void Charge(int count){AddBytes(count);if(++rows>200_000)throw new BudgetExceeded();}
    }
    private sealed record Event(DateTimeOffset At,string Model,long Input,long CacheWrite,long CacheRead,long Output,long Thinking)
    {public long Total=>checked(Input+CacheWrite+CacheRead+Output);}
    private sealed record Turn(long Index,string? Uuid,string? Bot,string? Response,Event Event,bool HasTime,bool AdditionalUsage);
    private sealed record StepInfo(string? Uuid,string? Bot,DateTimeOffset? At);
    private sealed record Attempt(bool Recognized,bool Partial,int Excluded,List<(long,Event,string?,string?)> Events);

    private static Attempt ReadDatabase(string file,Budget budget)
    {
        try {return ScanDatabase(file,budget,false);}
        catch(InvalidDataException) when(!File.Exists(file+"-wal"))
        {
            // Immutable is allowed only without WAL; reject if a writer changes the file/sidecar state.
            var stamp=Stamp(file);var result=ScanDatabase(file,budget,true);
            if(stamp!=Stamp(file)||File.Exists(file+"-wal"))throw new InvalidDataException();return result;
        }
    }
    private static string Stamp(string file)
    {
        using var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var header=new byte[100];var size=s.Read(header);var info=new FileInfo(file);
        return info.Length+":"+info.LastWriteTimeUtc.Ticks+":"+Convert.ToHexString(SHA256.HashData(header.AsSpan(0,size)))+":"+File.Exists(file+"-wal")+":"+File.Exists(file+"-shm");
    }
    private static Attempt ScanDatabase(string file,Budget budget,bool immutable)
    {
        using var db=new HistorySqlite(file,()=>budget.Stop,immutable);
        db.Execute("PRAGMA query_only=ON");db.Execute("PRAGMA trusted_schema=OFF");db.Execute("BEGIN");
        if(!SupportedTable(db,"gen_metadata","data",budget))return new(false,false,0,[]);
        var turns=new List<Turn>();var result=new List<(long,Event,string?,string?)>();var partial=false;var excluded=0;
        foreach(var row in db.Rows("SELECT idx, CASE WHEN typeof(data)='blob' AND length(data)<=1048576 THEN data END FROM main.gen_metadata NOT INDEXED"))
        {
            budget.Charge(0);
            try
            {
                if(HistorySqlite.Type(row,0)!=1)throw new InvalidDataException();
                var blob=HistorySqlite.Blob(row,1,1_048_576);budget.AddBytes(blob.Length);
                var turn=ParseTurn(HistorySqlite.Integer(row,0),blob);turns.Add(turn);
                if(turn.AdditionalUsage){partial=true;excluded++;} // Unmapped retry usage never establishes completeness.
            }
            catch(Exception e) when(e is InvalidDataException or OverflowException or ArgumentException or DecoderFallbackException) {partial=true;excluded++;}
        }
        var steps=new List<StepInfo>();
        if(turns.Any(t=>!t.HasTime))
        {
            if(SupportedTable(db,"steps","metadata",budget))
                foreach(var row in db.Rows("SELECT CASE WHEN typeof(metadata)='blob' AND length(metadata)<=1048576 THEN metadata END FROM main.steps NOT INDEXED"))
                {
                    budget.Charge(0);
                    try
                    {
                        var blob=HistorySqlite.Blob(row,0,1_048_576);budget.AddBytes(blob.Length);var f=Proto.Parse(blob,1,9,12);
                        steps.Add(new(f.String(12),f.Message(9,7).String(7),Timestamp(f.Message(1,1,2))));
                    }
                    catch(Exception e) when(e is InvalidDataException or OverflowException or ArgumentException or DecoderFallbackException) {partial=true;excluded++;}
                }
        }
        var bots=steps.Where(s=>s.Bot is not null).GroupBy(s=>s.Bot!).ToDictionary(g=>g.Key,g=>g.Distinct().ToArray());
        var uuids=steps.Where(s=>s.Uuid is not null).GroupBy(s=>s.Uuid!).ToDictionary(g=>g.Key,g=>g.Select(s=>s.At).Distinct().ToArray());
        var botUses=turns.Where(t=>t.Bot is not null).GroupBy(t=>t.Bot!).ToDictionary(g=>g.Key,g=>g.Count());
        var uuidUses=turns.Where(t=>t.Uuid is not null).GroupBy(t=>t.Uuid!).ToDictionary(g=>g.Key,g=>g.Count());
        foreach(var t in turns)
        {
            budget.Check();
            var ev=t.Event;
            if(!t.HasTime)
            {
                DateTimeOffset? at=null;
                if(t.Uuid is not null && t.Bot is not null && bots.TryGetValue(t.Bot,out var candidates))
                {
                    if(candidates.Length==1&&candidates[0].Uuid==t.Uuid && botUses[t.Bot]==1)at=candidates[0].At;
                }
                else if(t.Uuid is not null && uuidUses[t.Uuid]==1 && uuids.TryGetValue(t.Uuid,out var times) && times.Length==1)at=times[0];
                if(at is null){partial=true;excluded++;continue;}ev=ev with{At=at.Value};
            }
            if(t.Response is null&&(t.Uuid is null||t.Bot is null)){partial=true;excluded++;continue;}
            result.Add((t.Index,ev,t.Response,t.Response is null?"turn:"+t.Uuid+":"+t.Bot+":"+t.Index:null));
        }
        db.Execute("ROLLBACK");return new(true,partial,excluded,result);
    }
    private static bool SupportedTable(HistorySqlite db,string table,string payload,Budget budget)
    {
        var found=false;var n=0;
        foreach(var r in db.Rows("SELECT name,type,sql,rootpage FROM main.sqlite_master LIMIT 129"))
        {
            budget.Check();if(++n>128)throw new InvalidDataException();
            if(HistorySqlite.Text(r,0)!=table)continue;found=true;
            if(HistorySqlite.Text(r,1)!="table"||HistorySqlite.Integer(r,3)<=0||HistorySqlite.Text(r,2).Contains("VIRTUAL",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException();
        }
        if(!found)return false;
        var columns=new HashSet<string>();n=0;
        foreach(var r in db.Rows("PRAGMA main.table_xinfo("+table+")"))
        {budget.Check();if(++n>64||HistorySqlite.Integer(r,6)!=0)throw new InvalidDataException();columns.Add(HistorySqlite.Text(r,1));}
        if(!columns.Contains("idx")||!columns.Contains(payload))throw new InvalidDataException();return true;
    }
    private static Turn ParseTurn(long idx,byte[] blob)
    {
        var root=Proto.Parse(blob,1,4);var chat=root.Message(1,4,9,17,19);if(!root.Has(1)||!chat.Has(4))throw new InvalidDataException();
        var usage=chat.Message(4,1,2,3,4,5,7,9,10,11);
        // #1 is a model enum, #3 total output includes #9 thinking + #10 visible text.
        // Presence of a model alone does not establish a measured zero.
        if(!new[]{2,3,4,5,9,10}.Any(usage.Has))throw new InvalidDataException();
        var input=usage.Counter(2);var write=usage.Counter(4);var cached=usage.Counter(5);var thinking=usage.Counter(9);var text=usage.Counter(10);
        var output=usage.Has(3)?usage.Counter(3):checked(thinking+text);
        if(usage.Has(3)&&(usage.Has(9)||usage.Has(10))&&output!=checked(thinking+text))throw new InvalidDataException();
        var at=Timestamp(chat.Message(9,4).Message(4,1,2));
        var ev=new Event(at??default,chat.String(19)??"model-enum:"+usage.Counter(1),input,write,cached,output,thinking);_=ev.Total;
        return new(idx,root.String(4),usage.String(7),usage.String(11),ev,at is not null,chat.Has(17));
    }
    private static void ValidateTime(DateTimeOffset at){if(at<DateTimeOffset.UnixEpoch||at>DateTimeOffset.UtcNow.AddMinutes(5))throw new InvalidDataException();}
    private static DateTimeOffset? Timestamp(Proto p)
    {
        if(!p.Has(1))return null;var seconds=p.Counter(1);var nanos=p.Counter(2);
        if(seconds<=0||nanos>999_999_999)throw new InvalidDataException();var at=DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos/100);ValidateTime(at);return at;
    }
    // Tiny bounded protobuf wire reader: skips unrelated fields without decoding their text.
    private sealed class Proto
    {
        private readonly Dictionary<int,List<(int Wire,ulong Number,byte[]? Bytes)>> fields=[];
        internal bool Has(int n)=>fields.ContainsKey(n);
        internal static Proto Parse(byte[] b,params int[] keep)
        {
            var p=new Proto();var i=0;
            ulong Var(){ulong value=0;for(var n=0;n<10;n++){if(i>=b.Length)throw new InvalidDataException();var v=b[i++];if(n==9&&v>1)throw new InvalidDataException();value|=(ulong)(v&127)<<(7*n);if(v<128)return value;}throw new InvalidDataException();}
            while(i<b.Length)
            {
                var tag=Var();var number=tag>>3;var wire=(int)(tag&7);if(number is 0 or >536870911)throw new InvalidDataException();
                ulong value=0;byte[]? data=null;
                if(wire==0)value=Var();else
                {
                    var size=wire switch{1=>8,5=>4,2=>checked((int)Var()),_=>throw new InvalidDataException()};
                    if(size>b.Length-i)throw new InvalidDataException();
                    // Length-delimited bytes are only decoded when selected below.
                    if(wire==2&&keep.Contains((int)number))data=b.AsSpan(i,size).ToArray();i+=size;
                }
                if(!keep.Contains((int)number))continue;
                if(!p.fields.TryGetValue((int)number,out var list))p.fields[(int)number]=list=[];
                list.Add((wire,value,data));if(p.fields.Count>256||list.Count>256)throw new InvalidDataException();
            }
            return p;
        }
        internal long Counter(int n)
        {
            if(!fields.TryGetValue(n,out var list))return 0;
            foreach(var f in list)if(f.Wire!=0||f.Number>long.MaxValue)throw new InvalidDataException();return (long)list[^1].Number;
        }
        internal string? String(int n)
        {
            if(!fields.TryGetValue(n,out var list))return null;string? value=null;
            foreach(var f in list){if(f.Wire!=2||f.Bytes!.Length>4096)throw new InvalidDataException();value=new UTF8Encoding(false,true).GetString(f.Bytes);}
            return string.IsNullOrWhiteSpace(value)?null:value;
        }
        internal Proto Message(int n,params int[] keep)
        {
            var merged=new Proto();if(!fields.TryGetValue(n,out var list))return merged;
            foreach(var f in list)
            {
                if(f.Wire!=2)throw new InvalidDataException();var message=Parse(f.Bytes!,keep);
                foreach(var pair in message.fields){if(!merged.fields.TryGetValue(pair.Key,out var values))merged.fields[pair.Key]=values=[];values.AddRange(pair.Value);if(values.Count>256)throw new InvalidDataException();}
            }
            return merged;
        }
    }
    private static long JCounter(JsonElement r,bool required,params string[] keys)
    {
        long? value=null;
        foreach(var key in keys)if(r.TryGetProperty(key,out var v))
        {if(v.ValueKind!=JsonValueKind.Number||!v.TryGetInt64(out var count)||count<0||value is not null&&value!=count)throw new InvalidDataException();value=count;}
        if(required&&value is null)throw new InvalidDataException();return value??0;
    }
    private static string? JText(JsonElement r,params string[] keys)
    {
        string? value=null;
        foreach(var key in keys)if(r.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null)
        {if(v.ValueKind!=JsonValueKind.String)throw new InvalidDataException();var s=v.GetString();if(string.IsNullOrWhiteSpace(s))continue;if(s.Length>4096||value is not null&&value!=s)throw new InvalidDataException();value=s;}return value;
    }
    private static string? ReadLine(StreamReader reader,Budget budget)
    {
        var line=new StringBuilder();int ch;while((ch=reader.Read())>=0){if((line.Length&4095)==0)budget.Check();if(ch=='\n')break;if(line.Length>=1_048_576)throw new BudgetExceeded();line.Append((char)ch);}
        if(ch<0&&line.Length==0)return null;budget.Charge(Encoding.UTF8.GetByteCount(line.ToString()));return line.ToString();
    }
}
