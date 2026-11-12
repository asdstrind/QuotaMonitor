using System.Net;
using System.Text;
using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using ZstdSharp;

namespace QuotaMonitor.Tests;
public sealed class AdapterTests
{
    private static JsonElement Json(string text){using var doc=JsonDocument.Parse(text);return doc.RootElement.Clone();}
    [Fact]public void CodexUsesMultipleDynamicWindowsAndClampsRemaining()
    {
        var result=CodexAdapter.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":1}},"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":-5,"windowDurationMins":15}},"codex":{"primary":{"usedPercent":135,"windowDurationMins":300,"resetsAt":1791604039},"secondary":{"usedPercent":27,"windowDurationMins":10080}}}}"""),DateTimeOffset.UtcNow);
        Assert.Equal(0,result.RemainingPercent);Assert.Equal(3,result.Windows.Count);Assert.Equal(135,result.Windows.Single(x=>x.Bucket=="codex"&&x.Name=="Primary").UsedPercent);Assert.Equal(100,result.Windows.Single(x=>x.Bucket=="other").RemainingPercent);Assert.Equal(73,result.Windows.Single(x=>x.Name=="Secondary").RemainingPercent);
    }
    [Fact]public void MissingCodexWindowsAreUnavailableInsteadOfZero(){var result=CodexAdapter.Parse(Json("""{"rateLimits":{"primary":null,"secondary":null}}"""),DateTimeOffset.UtcNow);Assert.Equal(ConnectionStatus.Unavailable,result.ConnectionStatus);Assert.Null(result.RemainingPercent);}
    [Fact]public void LegacyCodexWindowKeepsUnknownDuration(){var result=CodexAdapter.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":25}}}"""),DateTimeOffset.UtcNow);Assert.Equal(75,result.RemainingPercent);Assert.Null(result.Windows.Single().DurationMinutes);}
    [Fact]public void CodexReadGuardBlocksWritesWithoutChangingBytes()
    {
        var directory=Path.Combine(Path.GetTempPath(),"quota-guard-"+Guid.NewGuid());Directory.CreateDirectory(directory);var file=Path.Combine(directory,"config.toml");File.WriteAllText(file,"non-secret-test");try{using(var guard=new CodexReadGuard(directory)){Assert.Throws<IOException>(()=>File.WriteAllText(file,"changed"));Assert.Equal("non-secret-test",File.ReadAllText(file));}File.WriteAllText(file,"released");Assert.Equal("released",File.ReadAllText(file));}finally{Directory.Delete(directory,true);}
    }
    [Fact]public void DefaultDshTariffNeverInventsCacheWritePricing(){var settings=new MonitorSettings();Assert.Equal(0.000012m,DshAdapter.Estimate([new(0,10,0,0,DateTimeOffset.UtcNow,"deepseek-flash")],settings));Assert.Null(DshAdapter.Estimate([new(0,10,0,1,DateTimeOffset.UtcNow,"deepseek-flash")],settings));}
    private static string Rows(params object[] rows)=>string.Join('\n',rows.Select(x=>JsonSerializer.Serialize(x)))+"\n";
    private static object Header(string id="session-a")=>new{type="session",version=4,id};
    private static object Context()=>new{type="request/context",seq=1,time=1791600000000L,data=new{provider="deepseek-account",model="deepseek-flash"}};
    private static object Message(int seq,int input,int output,int cached=0,int? total=null,string type="assistant/message")=>new{type,seq,time=1791600000000L+seq,data=new{turn=1,step=1,usage=new{inputTokens=input,outputTokens=output,cacheReadTokens=cached,cacheWriteTokens=0,reasoningTokens=output/2,totalTokens=total}}};
    [Fact]public void DshSettlementReplacementDoesNotDoubleCountCacheOrReasoning(){var session=DshAdapter.ParseLines(Rows(Header(),Context(),Message(2,100,50,500),Message(3,100,60,500)));var usage=Assert.Single(session.Usage);Assert.Equal(660,usage.Total);Assert.Equal(60,usage.Output);}
    [Fact]public void DshRetryCountsDistinctChargedAttempts(){var session=DshAdapter.ParseLines(Rows(Header(),Context(),Message(2,100,50),new{type="llm/retry-started",seq=3,time=1791600000003L,data=new{turn=1,step=1}},Message(4,200,60)));Assert.Equal(410,session.Usage.Sum(x=>x.Total));}
    [Fact]public void DshInheritedSeedIsExcluded(){var session=DshAdapter.ParseLines(Rows(new{type="session",version=4,id="child",isSeeded=true},Context(),Message(2,100,50),new{type="session/end-seed",seq=3,time=1791600000003L,data=new{inherited=true}},Message(4,200,60)));Assert.Equal(260,Assert.Single(session.Usage).Total);}
    [Fact]public void SeedWithoutBoundaryIsExplicitlyIncomplete(){var session=DshAdapter.ParseLines(Rows(new{type="session",version=4,id="child",isSeeded=true},Context(),Message(2,100,50)));Assert.Empty(session.Usage);Assert.Equal(1,session.InvalidRows);}
    [Fact]public void ContradictoryUsageIsRejected(){var session=DshAdapter.ParseLines(Rows(Header(),Context(),Message(2,100,50,500,151)));Assert.Empty(session.Usage);Assert.Equal(1,session.InvalidRows);}
    [Fact]public void MissingOrNonDeepSeekRouteIsNotInvented(){var session=DshAdapter.ParseLines(Rows(Header(),new{type="request/context",data=new{provider="other",model="other"}},Message(2,100,50)));Assert.Empty(session.Usage);}
    [Fact]public void DshReadsAllCompressedFramesAndExcludesTornTail()
    {
        var directory=Path.Combine(Path.GetTempPath(),"quota-test-"+Guid.NewGuid());Directory.CreateDirectory(directory);var file=Path.Combine(directory,"session.v4.jsonl.zstd");
        try{using var compressor=new Compressor();var a=compressor.Wrap(Encoding.UTF8.GetBytes(Rows(Header()))).ToArray();var b=compressor.Wrap(Encoding.UTF8.GetBytes(Rows(Context(),Message(2,100,50,500)))).ToArray();File.WriteAllBytes(file,a.Concat(b).Concat(b.Take(9)).ToArray());var read=DshAdapter.ReadSession(file,CancellationToken.None);Assert.Equal(650,Assert.Single(read.Usage).Total);Assert.True(read.TornTail);Assert.Equal(a.Length,DshAdapter.FrameLength(a));}finally{Directory.Delete(directory,true);}
    }
    [Fact]public void EstimateRequiresPriceBasisAndExactModel()
    {
        DshUsage[] usage=[new(100,10,1000,0,DateTimeOffset.UtcNow,"m")];var settings=new MonitorSettings{DshPricedModel=""};Assert.Null(DshAdapter.Estimate(usage,settings));settings.DshPricedModel="m";settings.DshPriceBasis="explicit verified tariff";settings.DshInputPerMillion=1;settings.DshCacheReadPerMillion=0.1m;settings.DshCacheWritePerMillion=1;settings.DshOutputPerMillion=2;Assert.Equal(0.00022m,DshAdapter.Estimate(usage,settings));settings.DshPricedModel="wrong";Assert.Null(DshAdapter.Estimate(usage,settings));
    }
    [Fact]public async Task RefreshFailureIsIsolatedAndDisposalCancelsWork()
    {
        await using var coordinator=new RefreshCoordinator(new(),[new FakeAdapter("codex",true),new FakeAdapter("dsh",false)]);await coordinator.RefreshAsync(true);Assert.Equal(ConnectionStatus.Unavailable,coordinator.Snapshots.Single(x=>x.ProviderId=="codex").ConnectionStatus);Assert.Equal(ConnectionStatus.Ready,coordinator.Snapshots.Single(x=>x.ProviderId=="dsh").ConnectionStatus);
    }
    [Fact]public async Task DshChoosesHighestGenerationAndRefreshIsIdempotent()
    {
        var directory=Path.Combine(Path.GetTempPath(),"quota-generation-"+Guid.NewGuid());Directory.CreateDirectory(directory);try{File.WriteAllText(Path.Combine(directory,"session.jsonl"),Rows(new{type="session",version=0,id="same-session"},Context(),Message(2,1000,500)));File.WriteAllText(Path.Combine(directory,"session.v4.jsonl"),Rows(Header("same-session"),Context(),Message(2,100,50)));var adapter=new DshAdapter(new(){DshSessionRoot=directory});var first=await adapter.RefreshAsync(true,CancellationToken.None);var second=await adapter.RefreshAsync(true,CancellationToken.None);Assert.Equal(150,first.TotalTokens);Assert.Equal(first.TotalTokens,second.TotalTokens);}finally{Directory.Delete(directory,true);}
    }
    [Fact]public async Task CoordinatorCancelsInFlightAdapterOnExit()
    {
        var adapter=new BlockingAdapter();var coordinator=new RefreshCoordinator(new(),[adapter]);coordinator.Start();await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));await coordinator.DisposeAsync();Assert.True(adapter.Cancelled);
    }
    private static HttpResponseMessage Response(string text)=>new(HttpStatusCode.OK){Content=new StringContent(text,Encoding.UTF8,"application/json")};
    private sealed class FakeHttp(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(respond(request));}
    private sealed class TestSecret : ISecretStore{public string? Read()=>"test-only";public void Write(string value){}public void Delete(){}}
    private sealed class EmptySecret : ISecretStore{public string? Read()=>null;public void Write(string value){}public void Delete(){}}
    private sealed class FakeAdapter(string id,bool fail) : IProviderAdapter{public string Id=>id;public Task<ProviderSnapshot> RefreshAsync(bool force,CancellationToken ct)=>fail?throw new ProviderException("intentional isolated test failure"):Task.FromResult(ProviderSnapshot.Empty(id,id,MetricType.Tokens) with{ConnectionStatus=ConnectionStatus.Ready});}
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider{public DateTimeOffset Now{get;set;}=now;public override DateTimeOffset GetUtcNow()=>Now.ToUniversalTime();}
    private sealed class BlockingAdapter : IProviderAdapter{public string Id=>"codex";public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Cancelled{get;private set;}public async Task<ProviderSnapshot> RefreshAsync(bool force,CancellationToken ct){Entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){Cancelled=true;throw;}return ProviderSnapshot.Empty(Id,Id,MetricType.Quota);}}
}
