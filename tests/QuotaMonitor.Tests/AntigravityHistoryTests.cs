using System.Text;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;

namespace QuotaMonitor.Tests;

public sealed class AntigravityHistoryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"QuotaMonitor-AG-"+Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-10-14T18:30:00Z");
    private static readonly DateOnly Today=new(2026,10,17);
    private static readonly HistoryRequest Request=new(Today,Today.AddDays(-7),true);
    private string Db(string name="a")=>Path.Combine(root,"antigravity","conversations",name+".db");
    private string Cache=>Path.Combine(root,"tokscale");
    private UsageHistory Read(TimeZoneInfo? zone=null)=>new AntigravityHistorySource(root,Cache,zone??TimeZoneInfo.Utc).Read(Request);
    private HistorySqlite Create(string name="a",string? schema=null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Db(name))!);
        var db=new HistorySqlite(Db(name),writableFixture:true);
        db.Execute(schema??"CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY,data BLOB)");return db;
    }
    private static byte[] V(ulong v){var b=new List<byte>();while(v>=128){b.Add((byte)((v&127)|128));v>>=7;}b.Add((byte)v);return b.ToArray();}
    private static byte[] N(int n,long v)=>[..V((ulong)n<<3),..V((ulong)v)];
    private static byte[] B(int n,params byte[][] chunks){var b=chunks.SelectMany(x=>x).ToArray();return [..V(((ulong)n<<3)|2),..V((ulong)b.Length),..b];}
    private static byte[] S(int n,string text)=>B(n,Encoding.UTF8.GetBytes(text));
    private static byte[] Turn(long input=100,long cache=50,long output=30,long thinking=10,string response="r1",string model="gemini-test",bool timestamp=true,string uuid="u1",string bot="b1",DateTimeOffset? at=null,long write=0)=>
        [..B(1,B(4,N(1,1320),N(2,input),N(3,output),N(4,write),N(5,cache),N(9,thinking),N(10,output-thinking),S(7,bot),S(11,response)),S(19,model),timestamp?B(9,B(4,N(1,(at??At).ToUnixTimeSeconds()))):[]),..S(4,uuid)];
    private static void Insert(HistorySqlite db,int idx,byte[] blob,string table="gen_metadata",string column="data")=>db.Execute($"INSERT INTO {table}(idx,{column}) VALUES({idx},X'{Convert.ToHexString(blob)}')");
    private void Json(string text)
    {var dir=Path.Combine(Cache,"antigravity-cache","sessions");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"a.jsonl"),text);}
    private static byte[] SharedBytes(string path){using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var m=new MemoryStream();f.CopyTo(m);return m.ToArray();}
    [Fact] public void AbsentAndForeignDatabasesAreUnavailableNotZero()
    {
        Assert.Empty(Read().Days);
        using(var db=Create(schema:"CREATE TABLE summary(id INTEGER)")){};
        var h=Read();Assert.Empty(h.Days);Assert.Contains("Unavailable",h.Error);
    }
    [Fact] public void EmptySchemaCannotEstablishDatedCoverage()
    {using(var db=Create()){};var h=Read();Assert.NotNull(h.Error);Assert.Empty(h.Days);}
    [Fact] public void SuccessfulDatedHistoryEstablishesZerosWithinCoverageOnly()
    {
        using(var db=Create())Insert(db,0,Turn());var h=Read();Assert.Null(h.Error);
        Assert.Equal(new DateOnly(2026,10,14),h.CoverageStart);
        Assert.Equal(4,h.Days.Count);Assert.All(h.Days.Skip(1),d=>Assert.Equal(0,d.Tokens));
        var vm=new MonitorViewModel();vm.UpdateHistory(h,Today);
        Assert.Null(vm.Grid!.Days.Single(d=>d.Date==Today.AddDays(-4)).Lanes.Single(l=>l.ProviderId=="antigravity").Tokens);
        Assert.Equal("0",vm.BuildTip(vm.Grid.Days[^1]).Rows.Single(r=>r.Name=="Antigravity").Value);
    }
    [Fact] public void InputCacheWriteReadAndOutputAreCountedOnceModelIdIsNotTokens()
    {
        using(var db=Create())Insert(db,0,Turn(write:7));
        var d=Read().Days.Single(d=>d.Tokens>0);Assert.Equal(187,d.Tokens);Assert.Equal(50,d.CachedTokens);Assert.Equal(1,d.Requests);Assert.Equal(UsageQuality.Exact,d.Quality);
    }
    [Fact] public void DateUsesEventTimestampAndRequestedTimezone()
    {
        using(var db=Create())Insert(db,0,Turn());
        Assert.Equal(new DateOnly(2026,10,14),Read().Days.Single(d=>d.Tokens>0).Date);
        var china=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        Assert.Equal(new DateOnly(2026,10,15),Read(china).Days.Single(d=>d.Tokens>0).Date);
    }
    [Fact] public void CopiesAndRepeatedRequestIdsDeduplicateAcrossDifferentConversations()
    {
        using(var db=Create())Insert(db,0,Turn());
        var copy=Path.Combine(root,"antigravity","a.db");File.Copy(Db(),copy);
        using(var db=Create("different"))Insert(db,9,Turn());
        var h=Read();Assert.Null(h.Error);Assert.Equal(180,h.Days.Sum(d=>d.Tokens));Assert.Equal(1,h.Days.Sum(d=>d.Requests));
    }
    [Fact] public void DistinctRequestsAcrossSessionsAndModelsRemainDistinct()
    {
        using(var db=Create()){Insert(db,0,Turn());Insert(db,1,Turn(response:"r2",model:"claude-test",at:At.AddDays(1)));}
        Assert.Equal(360,Read().Days.Sum(d=>d.Tokens));Assert.Equal(2,Read().Days.Count(d=>d.Tokens>0));
    }
    [Fact] public void RenamedCopyWithoutResponseIdUsesStableGenerationIdentity()
    {
        using(var db=Create())Insert(db,0,Turn(response:""));File.Copy(Db(),Db("renamed"));
        Assert.Null(Read().Error);Assert.Equal(180,Read().Days.Sum(d=>d.Tokens));
    }
    [Fact] public void ConflictingCopiesExcludeBothInsteadOfPickingLargerOrFirst()
    {
        using(var db=Create()){Insert(db,0,Turn());Insert(db,1,Turn(response:"good"));}
        using(var db=Create("copy"))Insert(db,0,Turn(input:900));
        var h=Read();Assert.Contains("Partial",h.Error);Assert.Equal(180,h.Days.Sum(d=>d.Tokens));Assert.All(h.Days,d=>Assert.Equal(UsageQuality.Partial,d.Quality));
    }
    [Fact] public void ExactStepBotJoinRecoversMissingEmbeddedTimeWithoutReadingPayload()
    {
        using(var db=Create())
        {
            Insert(db,0,Turn(timestamp:false));db.Execute("CREATE TABLE steps(idx INTEGER PRIMARY KEY,metadata BLOB,step_payload BLOB)");
            Insert(db,7,[..B(1,N(1,At.ToUnixTimeSeconds())),..B(9,S(7,"b1")),..S(12,"u1")],"steps","metadata");
        }
        var h=Read();Assert.Null(h.Error);Assert.Equal(180,h.Days.Sum(d=>d.Tokens));
    }
    [Fact] public void AmbiguousStepIdentityNeverSubstitutesFileOrSessionTime()
    {
        using(var db=Create())
        {
            Insert(db,0,Turn(timestamp:false));Insert(db,1,Turn(response:"good"));db.Execute("CREATE TABLE steps(idx INTEGER PRIMARY KEY,metadata BLOB)");
            for(var i=0;i<2;i++)Insert(db,i,[..B(1,N(1,At.AddDays(i).ToUnixTimeSeconds())),..B(9,S(7,"b1")),..S(12,"u1")],"steps","metadata");
        }
        var h=Read();Assert.Contains("Partial",h.Error);Assert.Equal(180,h.Days.Sum(d=>d.Tokens));
    }
    [Fact] public void UnsupportedOpaqueTimestampRemainsUnavailable()
    {using(var db=Create())Insert(db,0,Turn(timestamp:false));Assert.Empty(Read().Days);Assert.Contains("Unavailable",Read().Error);}
    [Fact] public void MalformedRowKeepsKnownLowerBoundAndDoesNotCreateZeroDays()
    {
        using(var db=Create()){Insert(db,0,Turn());Insert(db,1,[255]);}
        var h=Read();Assert.Single(h.Days);Assert.Equal(180,h.Days[0].Tokens);Assert.Contains("Partial",h.Error);Assert.Equal(UsageQuality.Partial,h.Days[0].Quality);
    }
    [Fact] public void OutputBreakdownMismatchIsExcluded()
    {
        var bad=B(1,B(4,N(2,100),N(3,30),N(9,10),N(10,30)),B(9,B(4,N(1,At.ToUnixTimeSeconds()))));
        using(var db=Create())Insert(db,0,bad);Assert.Empty(Read().Days);
    }
    [Fact] public void ExplicitZeroIsDifferentFromPartialZero()
    {
        using(var db=Create())Insert(db,0,Turn(0,0,0,0));Assert.Null(Read().Error);Assert.All(Read().Days,d=>Assert.Equal(0,d.Tokens));
        using(var db=new HistorySqlite(Db(),writableFixture:true))Insert(db,1,[255]);Assert.Empty(Read().Days);
    }
    [Fact] public void UnknownSchemaAndViewsCannotEstablishCoverage()
    {using(var db=Create(schema:"CREATE TABLE real(idx INTEGER,data BLOB)")){db.Execute("CREATE VIEW gen_metadata AS SELECT * FROM real");}Assert.Empty(Read().Days);Assert.Contains("Unavailable",Read().Error);}
    [Fact] public void GeneratedColumnsAreRejectedBeforeReadingPayloads()
    {using(var db=Create(schema:"CREATE TABLE gen_metadata(idx INTEGER,data BLOB,calc INTEGER GENERATED ALWAYS AS (length(data)))")){}Assert.Empty(Read().Days);}
    [Fact] public void WithoutRowidAndOrdinaryExtraColumnsAreSupported()
    {using(var db=Create(schema:"CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY,data BLOB,extra INTEGER) WITHOUT ROWID"))Insert(db,0,Turn());Assert.Null(Read().Error);Assert.Equal(180,Read().Days.Sum(d=>d.Tokens));}
    [Fact] public void WalSnapshotIncludesCommittedRowsExcludesUncommittedWriterAndPreservesBytes()
    {
        using var writer=Create();writer.Execute("PRAGMA journal_mode=WAL");Insert(writer,0,Turn());
        var dbBefore=SharedBytes(Db());var walBefore=SharedBytes(Db()+"-wal");
        writer.Execute("BEGIN IMMEDIATE");Insert(writer,1,Turn(response:"uncommitted"));
        var h=Read();Assert.Null(h.Error);Assert.Equal(180,h.Days.Sum(d=>d.Tokens));writer.Execute("ROLLBACK");
        Assert.Equal(dbBefore,SharedBytes(Db()));Assert.Equal(walBefore,SharedBytes(Db()+"-wal"));
        Insert(writer,1,Turn(response:"committed"));Assert.Equal(360,Read().Days.Sum(d=>d.Tokens));
    }
    [Fact] public void CancellationReleasesHandlesAndDoesNotPublishTruncation()
    {using(var db=Create())Insert(db,0,Turn());using var c=new CancellationTokenSource();c.Cancel();Assert.Throws<OperationCanceledException>(()=>new AntigravityHistorySource(root,Cache).Read(Request,c.Token));using var file=new FileStream(Db(),FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
    [Fact] public void DatabasePresencePreventsJsonlFallbackEvenWhenCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Db())!);File.WriteAllText(Db(),"broken");
        Json("{\"type\":\"usage\",\"sessionId\":\"s\",\"timestamp\":1792000000000,\"input\":100,\"output\":20}\n");Assert.Empty(Read().Days);
    }
    [Fact] public void JsonlNormalizedCacheIsAlwaysLowerBoundAndReasoningOverlapIsRejected()
    {
        Json($"{{\"type\":\"session_meta\",\"sessionId\":\"s\",\"modelId\":\"gemini\"}}\n{{\"type\":\"usage\",\"timestamp\":{At.ToUnixTimeMilliseconds()},\"input\":100,\"output\":20,\"cacheRead\":5}}\n{{\"type\":\"usage\",\"timestamp\":{At.ToUnixTimeMilliseconds()},\"input\":100,\"output\":20,\"reasoning\":10}}\n");
        var h=Read();Assert.Contains("Partial",h.Error);Assert.Equal(125,h.Days.Sum(d=>d.Tokens));
    }
    [Fact] public void AggregateOverflowRemainsUnknown()
    {using(var db=Create()){Insert(db,0,Turn(input:long.MaxValue-200));Insert(db,1,Turn(input:long.MaxValue-200,response:"r2"));}Assert.Empty(Read().Days);Assert.Contains("溢出",Read().Error);}
    [Fact] public void NumerousUnrelatedFieldsAreSkippedWithoutDecodingOrLosingUsage()
    {
        var unrelated=Enumerable.Range(0,336).Select(_=>B(2,[255,254,253])).SelectMany(b=>b).ToArray();
        using(var db=Create())Insert(db,0,[..Turn(),..unrelated]);Assert.Null(Read().Error);Assert.Equal(180,Read().Days.Sum(d=>d.Tokens));
    }
    [Fact] public async Task IncompleteRefreshRetainsLastCompleteHistoryAsExplicitlyStale()
    {
        using(var db=Create())Insert(db,0,Turn());var source=new AntigravityHistorySource(root,Cache,TimeZoneInfo.Utc);
        var previous=await source.ReadHistoryAsync(null,Request,default);
        using(var db=new HistorySqlite(Db(),writableFixture:true))Insert(db,1,[255]);
        var retained=await source.ReadHistoryAsync(previous,Request,default);
        Assert.Contains("Stale",retained.Error);Assert.Equal(previous.UpdatedAt,retained.UpdatedAt);Assert.Equal(180,retained.Days.Sum(d=>d.Tokens));Assert.All(retained.Days,d=>Assert.Equal(UsageQuality.Partial,d.Quality));
        var again=await source.ReadHistoryAsync(retained,Request,default);Assert.Equal(retained.Note,again.Note);Assert.Equal(180,again.Days.Sum(d=>d.Tokens));
    }
    [Fact] public void UnmappedRetryUsageMarksPrimaryCountersAsLowerBound()
    {using(var db=Create())Insert(db,0,[..Turn(),..B(1,B(17,B(2,N(2,777))))]);var h=Read();Assert.Contains("Partial",h.Error);Assert.Equal(180,h.Days.Sum(d=>d.Tokens));}
    [Fact] public void BudgetExhaustionWithholdsTruncatedHistory()
    {for(var i=0;i<501;i++){Directory.CreateDirectory(Path.GetDirectoryName(Db())!);File.WriteAllBytes(Db(i.ToString()),[]);}Assert.Empty(Read().Days);Assert.Contains("上限",Read().Error);}
    [Fact] public void AntigravityCanDominateCombinedAndHoverHeatmapsAndShowsLowerBound()
    {
        var vm=new MonitorViewModel();var h=new UsageHistory{ProviderId="antigravity",Days=[new(Today,900,1,null,UsageQuality.Partial)],Error="Partial"};
        vm.UpdateHistory(h,Today);vm.UpdateHistory(new(){ProviderId="codex",Days=[new(Today,100,1,null,UsageQuality.Exact)]},Today);
        Assert.Equal("antigravity",vm.Grid!.Days[^1].Dominant!.ProviderId);Assert.Equal(1000,vm.Grid.Days[^1].Total);Assert.True(vm.Grid.Days[^1].Incomplete);
        Assert.Equal("900",vm.Lanes.Single(l=>l.Id=="antigravity").Total);Assert.True(vm.Lanes.Single(l=>l.Id=="antigravity").HasIssue);vm.HoverLane="antigravity";var tip=vm.BuildTip(vm.Grid.Days[^1]);Assert.Equal(4,tip.Rows.Count);Assert.Equal("900",tip.Rows.Single(r=>r.Name=="Antigravity").Value);Assert.All(tip.Rows,r=>Assert.Null(r.Flag));Assert.Equal("合计 1,000 Token",tip.Note);
        vm.HoverLane=null;Assert.Equal(4,vm.BuildTip(vm.Grid.Days[^1]).Rows.Count);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
