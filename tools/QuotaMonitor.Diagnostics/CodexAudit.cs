using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using ZstdSharp;

// Independent, local-only accounting audit. No conversation text, IDs or paths in the report.
internal static class CodexAudit
{
    public static async Task Run(string[] args)
    {
        var home=args.SkipWhile(x=>x!="--home").Skip(1).FirstOrDefault()??CodexUsageHistory.DefaultHome(); var today=DateOnly.FromDateTime(DateTime.Now);
        var outputFile=Path.GetFullPath(args.SkipWhile(x=>x!="--output").Skip(1).FirstOrDefault()??"evidence/v1.6.6/codex-audit.json");
        var snapshot=args.Contains("--snapshot")?Path.Combine(Path.GetDirectoryName(outputFile)!,"accounting-snapshot"):null;
        static string Anon(string? id)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id??"")));
        var start=UsageGrid.WindowStart(today,26);
        var paths=CodexUsageHistory.RolloutFiles(home).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var counts=new Dictionary<string,long>(); var shapes=new Dictionary<string,int>();
        void Count(string name,long by=1)=>counts[name]=counts.GetValueOrDefault(name)+by;
        var rows=new List<Row>();var fingerprints=new HashSet<string>();var summaries=new List<object>();var samples=new List<object>();
        var versions=new Dictionary<string,int>();
        foreach(var path in paths)
        {
            string? snapshotPath=null;
            if(snapshot is not null){var sd=Path.Combine(snapshot,Path.GetRelativePath(home,path).StartsWith("archived_sessions")?"archived_sessions":"sessions");Directory.CreateDirectory(sd);snapshotPath=Path.Combine(sd,$"rollout-audit-{summaries.Count:D4}.jsonl");File.WriteAllText(snapshotPath,"");}
            var local=new List<Row>();int malformed=0,usageLines=0;bool failed=false;string session="";long? previous=null;
            try
            {
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var decoded=path.EndsWith(".zst")?new DecompressionStream(file):null;
                using var reader=new StreamReader(decoded is null?file:decoded);
                while(await reader.ReadLineAsync() is {} line)
                {
                    // Only accounting and structural metadata. Never serialize a raw row.
                    if(!line.Contains("\"token_usage_record\"")&&!line.Contains("\"token_count\"")&&!line.Contains("\"session_meta\""))continue;
                    JsonDocument doc;try{doc=JsonDocument.Parse(line);}catch(JsonException){malformed++;continue;}
                    using(doc)
                    {
                        var root=doc.RootElement;var payload=root.Field("payload"); if(payload is null)continue;
                        if(root.Text("type")=="session_meta")
                        {
                            session=payload.Value.Text("id")??session;
                            var v=payload.Value.Text("cli_version")??"unspecified";versions[v]=versions.GetValueOrDefault(v)+1;
                            if(snapshotPath is not null)File.AppendAllText(snapshotPath,JsonSerializer.Serialize(new{type="session_meta",payload=new{id=Anon(session),cli_version=v}})+"\n");continue;
                        }
                        bool record=root.Text("type")=="token_usage_record";
                        if(!record&&(root.Text("type")!="event_msg"||payload.Value.Text("type")!="token_count"))continue;
                        if(snapshotPath is not null)File.AppendAllText(snapshotPath,record?
                            JsonSerializer.Serialize(new{timestamp=root.Text("timestamp"),type="token_usage_record",payload=new{response_id=Anon(payload.Value.Text("response_id")),usage=payload.Value.Field("usage")}})+"\n":
                            JsonSerializer.Serialize(new{timestamp=root.Text("timestamp"),type="event_msg",payload=new{type="token_count",info=payload.Value.Field("info")}})+"\n");
                        usageLines++;Count(record?"record_lines":"count_lines");
                        var usage=record?payload.Value.Field("usage"):payload.Value.Field("info")?.Field("last_token_usage");
                        if(usage is not { ValueKind:JsonValueKind.Object }){Count("no_usage_object");continue;}
                        var shape=(record?"record:":"count:")+string.Join(',',usage.Value.EnumerateObject().Select(p=>p.Name).Order());shapes[shape]=shapes.GetValueOrDefault(shape)+1;
                        if(!DateTimeOffset.TryParse(root.Text("timestamp"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at)){Count("invalid_timestamp");continue;}
                        var input=usage.Value.Count("input_tokens");var output=usage.Value.Count("output_tokens");var total=usage.Value.Count("total_tokens");
                        var cache=usage.Value.Count("cached_input_tokens");var reasoning=usage.Value.Count("reasoning_output_tokens");
                        if(input is {} i&&output is {} o&&total is {} t)
                        {
                            Count(t==i+o?"total_equals_input_plus_output":"total_mismatch");
                            Count(cache<=input?"cache_subset_input":"cache_not_subset_input");Count(reasoning<=output?"reasoning_subset_output":"reasoning_not_subset_output");
                            if(t!=i+o&&samples.Count<30)samples.Add(new{Kind="field-mismatch",At=at,Input=input,Output=output,Total=total,Cache=cache,Reasoning=reasoning,CacheWrite=usage.Value.Count("cache_write_input_tokens")});
                        }
                        else Count("incomplete_usage_fields");
                        long? cumulative=record?null:payload.Value.Field("info")?.Field("total_token_usage")?.Count("total_tokens");
                        long? delta=record?total??(input+output):cumulative==previous?0:previous is {} p&&cumulative>=p?cumulative-p:total;
                        if(!record&&total!=input+output)Count(delta==0?"field_mismatch_reemitted_unchanged_counter":"field_mismatch_changed_counter");
                        if(!record&&previous is null&&cumulative>total)Count("initial_unattributed_counter_gap",cumulative.Value-total!.Value);
                        if(!record){if(previous is null&&cumulative!=total)Count("initial_counter_exceeds_last");if(cumulative<previous)Count("counter_resets");if(previous is {} prev&&cumulative>prev&&cumulative-prev!=total)Count("delta_differs_last");previous=cumulative;}
                        if(delta is null){malformed++;continue;}
                        var key=record?"R|"+(payload.Value.Text("response_id")??(session+"|"+root.Text("timestamp")+"|"+payload.Value.Text("turn_id")+"|"+total)):
                            "L|"+session+"|"+root.Text("timestamp")+"|"+cumulative+"|"+total;
                        local.Add(new(key,DateOnly.FromDateTime(at.LocalDateTime),delta.Value,input??0,output??0,cache??0,reasoning??0,record,root.Text("timestamp")??"",session,cumulative,total));
                        if(record&&total!=input+output&&samples.Count<30)samples.Add(new{Kind="field-mismatch",At=at,Input=input,Output=output,Total=total,Cache=cache,Reasoning=reasoning,CacheWrite=usage.Value.Count("cache_write_input_tokens")});
                    }
                }
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdException){failed=true;Count("read_failures");}
            var fp=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',local.Select(r=>r.Key)))));
            bool duplicate=local.Count>0&&!fingerprints.Add(fp);
            if(duplicate)Count("duplicate_usage_files");
            if(malformed>0)Count("partial_files");else if(!failed)Count("successful_files");
            if(local.Count==0)Count("files_without_usage");
            summaries.Add(new{Index=summaries.Count+1,Compressed=path.EndsWith(".zst"),Archive=Path.GetRelativePath(home,path).StartsWith("archived_sessions"),UsageLines=usageLines,ValidEvents=local.Count,Malformed=malformed,Failed=failed,Duplicate=duplicate,RecordTokens=local.Where(r=>r.Record).Sum(r=>r.Tokens),CountTokens=local.Where(r=>!r.Record).Sum(r=>r.Tokens),RecordEvents=local.Count(r=>r.Record),CountEvents=local.Count(r=>!r.Record),LastCounter=local.LastOrDefault(r=>!r.Record)?.Cumulative});
            if(local.Count(r=>r.Record)>0&&local.Count(r=>!r.Record)>0&&samples.Count<3)
                samples.Add(new{Kind="mixed-timeline",Index=summaries.Count,Events=local.Take(32).Select(r=>new{r.Record,r.Timestamp,r.Tokens,r.Input,r.Output,r.Cached,r.Reasoning,r.Cumulative,r.Last}).ToArray()});
            rows.AddRange(local);
            Count("records_plus_legacy_prefix",local.Where(r=>r.Record).Sum(r=>r.Tokens)+local.TakeWhile(r=>!r.Record).Sum(r=>r.Tokens));
            Count("legacy_prefix_tokens",local.Any(r=>r.Record)?local.TakeWhile(r=>!r.Record).Sum(r=>r.Tokens):0);
        }
        // Compare both formats by verified response identity / timestamp & amount, never add cached or reasoning again.
        var unique=rows.GroupBy(r=>r.Key).Select(g=>g.First()).ToArray();Count("duplicate_events",rows.Count-unique.Length);
        var records=unique.Where(r=>r.Record).ToArray();var legacy=unique.Where(r=>!r.Record&&r.Tokens>0).ToArray();
        var canonical=new List<Row>(records);var unmatched=new List<object>();
        foreach(var group in unique.GroupBy(r=>r.Session))
        {
            var interval=new List<Row>();bool hasRecord=false;
            foreach(var row in group.OrderBy(r=>r.Timestamp,StringComparer.Ordinal).ThenByDescending(r=>r.Record))
            {
                if(row.Record){interval.Add(row);hasRecord=true;continue;}
                if(row.Tokens==0)continue;
                if(row.Last!=row.Input+row.Output){Count("excluded_inconsistent_changed_counter",row.Tokens);continue;}
                var value=row.Tokens;
                var matching=interval.LastOrDefault(r=>r.Last==row.Last&&r.Input==row.Input&&r.Output==row.Output&&r.Cached==row.Cached);
                if(matching is not null){value=0;interval.Remove(matching);Count("matched_counter_mirrors");}
                if(value>0){canonical.Add(row with{Tokens=value});if(hasRecord){Count("unmatched_count_after_record",value);if(unmatched.Count<12)unmatched.Add(new{row.Timestamp,Tokens=value,row.Input,row.Output,row.Cached,row.Last,Pending=interval.Select(r=>new{r.Timestamp,r.Tokens,r.Input,r.Output,r.Cached}).ToArray()});}}
            }
        }
        var all=canonical.Where(r=>r.Day<=today).ToArray();
        var readerHistory=new CodexUsageHistory().Read(home,today,CancellationToken.None);
        UsageHistory? baselineHistory=null;
        var baselineCore=args.SkipWhile(x=>x!="--baseline-core").Skip(1).FirstOrDefault();
        if(baselineCore is not null){
            var context=new System.Runtime.Loader.AssemblyLoadContext("Quota165Audit",true);
            var assembly=context.LoadFromAssemblyPath(Path.GetFullPath(baselineCore));
            var type=assembly.GetType("QuotaMonitor.Core.Services.CodexUsageHistory")!;
            var instance=Activator.CreateInstance(type,[TimeZoneInfo.Local])!;
            var resultHistory=type.GetMethod("Read")!.Invoke(instance,[home,today,CancellationToken.None])!;
            baselineHistory=JsonSerializer.Deserialize<UsageHistory>(JsonSerializer.Serialize(resultHistory,resultHistory.GetType()));
            context.Unload();
        }
        var result=new{At=DateTimeOffset.Now,Today=today,WindowStart=start,FileCount=paths.Length,Counts=counts,Shapes=shapes,Versions=versions,
            Earliest=all.Length>0?all.Min(r=>r.Day):(DateOnly?)null,Latest=all.Length>0?all.Max(r=>r.Day):(DateOnly?)null,
            IndependentWindow=all.Where(r=>r.Day>=start).Sum(r=>r.Tokens),IndependentAll=all.Sum(r=>r.Tokens),BeforeWindow=all.Where(r=>r.Day<start).Sum(r=>r.Tokens),
            RecordTokens=records.Sum(r=>r.Tokens),LegacyTokens=legacy.Sum(r=>r.Tokens),
            ReaderWindow=readerHistory.Days.Where(d=>d.Date>=start).Sum(d=>d.Tokens??0),ReaderAll=readerHistory.Days.Sum(d=>d.Tokens??0),ReaderCoverage=readerHistory.CoverageStart,
            ReaderEstimatedDays=readerHistory.Days.Count(d=>d.Quality==UsageQuality.Estimated),ReaderPartialDays=readerHistory.Days.Count(d=>d.Quality==UsageQuality.Partial),ReaderNote=readerHistory.Note,
            CanonicalInput=all.Sum(r=>r.Input),CanonicalOutput=all.Sum(r=>r.Output),CanonicalCachedInputSubset=all.Sum(r=>r.Cached),CanonicalReasoningOutputSubset=all.Sum(r=>r.Reasoning),
            BaselineAll=baselineHistory?.Days.Sum(d=>d.Tokens??0),BaselineWindow=baselineHistory?.Days.Where(d=>d.Date>=start).Sum(d=>d.Tokens??0),
            SameDailyAmountsAsBaseline=baselineHistory is null?(bool?)null:baselineHistory.Days.Select(d=>(d.Date,d.Tokens)).SequenceEqual(readerHistory.Days.Select(d=>(d.Date,d.Tokens))),
            UnmatchedCountSamples=unmatched,Samples=samples,Files=summaries};
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);await File.WriteAllTextAsync(outputFile,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Local accounting audit completed; redacted aggregate report saved.");
    }
    private sealed record Row(string Key,DateOnly Day,long Tokens,long Input,long Output,long Cached,long Reasoning,bool Record,string Timestamp,string Session,long? Cumulative,long? Last);
}
