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
    [Theory]
    [InlineData(2500000,500000,5)]
    [InlineData(1,500000,0.000002)]
    [InlineData(100,1000,0.1)]
    [InlineData(0,500000,0)]
    public void QuotaConversionPreservesSmallAmounts(decimal quota,decimal ratio,decimal expected)=>Assert.Equal(expected,ClaudeCNAdapter.ToUsd(quota,ratio));
    [Fact]public void InvalidRatioRejected()=>Assert.Throws<ArgumentOutOfRangeException>(()=>ClaudeCNAdapter.ToUsd(1,0));
    [Fact]public void CodexUsesMultipleDynamicWindowsAndClampsRemaining()
    {
        var result=CodexAdapter.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":1}},"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":-5,"windowDurationMins":15}},"codex":{"primary":{"usedPercent":135,"windowDurationMins":300,"resetsAt":1791604039},"secondary":{"usedPercent":27,"windowDurationMins":10080}}}}"""),DateTimeOffset.UtcNow);
        Assert.Equal(0,result.RemainingPercent);Assert.Equal(3,result.Windows.Count);Assert.Equal(135,result.Windows.Single(x=>x.Bucket=="codex"&&x.Name=="Primary").UsedPercent);Assert.Equal(100,result.Windows.Single(x=>x.Bucket=="other").RemainingPercent);Assert.Equal(73,result.Windows.Single(x=>x.Name=="Secondary").RemainingPercent);
    }
    [Fact]public void MissingCodexWindowsAreUnavailableInsteadOfZero(){var result=CodexAdapter.Parse(Json("""{"rateLimits":{"primary":null,"secondary":null}}"""),DateTimeOffset.UtcNow);Assert.Equal(ConnectionStatus.Unavailable,result.ConnectionStatus);Assert.Null(result.RemainingPercent);}
    [Fact]public void LegacyCodexWindowKeepsUnknownDuration(){var result=CodexAdapter.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":25}}}"""),DateTimeOffset.UtcNow);Assert.Equal(75,result.RemainingPercent);Assert.Null(result.Windows.Single().DurationMinutes);}
    [Fact]public void ClaudeLogsExcludeRechargesAndPreserveMissingTokens(){var logs=ClaudeCNAdapter.ParseLogs(Json("""{"items":[{"type":1,"quota":100,"model_name":"recharge"},{"type":2,"quota":1,"model_name":"claude-real","prompt_tokens":123,"other":"{\"billing_source\":\"subscription\"}"}]}"""),500000);var item=Assert.Single(logs);Assert.Equal("claude-real",item.Model);Assert.Equal(0.000002m,item.UsageCost);Assert.Null(item.OutputTokens);Assert.Equal("subscription",item.BillingSource);}
    [Fact]public void UnknownClaudeLogShapeIsDiagnosed()=>Assert.Throws<ProviderException>(()=>ClaudeCNAdapter.ParseLogs(Json("{}"),500000));
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
    [Fact]public async Task ClaudeRequestsUseCorrectAuthAndSubscriptionAmounts()
    {
        var handler=new FakeHttp(request=>{
            Assert.Equal("https",request.RequestUri!.Scheme);Assert.Equal("claudecn.top",request.RequestUri.Host);Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);Assert.Equal("test-only",request.Headers.Authorization.Parameter);Assert.Equal("42",request.Headers.GetValues("New-Api-User").Single());
            var data=request.RequestUri.AbsolutePath switch{"/api/user/self"=>"""{"quota":2499999,"used_quota":1,"billing_preference":"subscription_first"}""","/api/log/self/stat"=>"""{"quota":1,"total_requests":1,"total_tokens":900}""",_=>"{\"items\":[]}"};
            if(request.RequestUri.AbsolutePath.EndsWith("stat")){Assert.Contains("start_timestamp=",request.RequestUri.Query);Assert.Contains("end_timestamp=",request.RequestUri.Query);Assert.Contains("type=2",request.RequestUri.Query);}
            return Response("{\"success\":true,\"data\":"+data+"}");});
        using var http=new HttpClient(handler);var settings=new MonitorSettings{ClaudeUserId="42"};var adapter=new ClaudeCNAdapter(http,settings,new TestSecret());var result=await adapter.RefreshAsync(true,CancellationToken.None);
        Assert.Equal(4.999998m,result.RemainingBalance);Assert.Equal(0.000002m,result.TodayCost);Assert.Equal(900,result.TodayTokens);Assert.Equal("subscription_first",result.BillingPreference);Assert.Contains("不等同",result.Note);Assert.Equal(ConnectionStatus.Ready,result.ConnectionStatus);
    }
    [Fact]public async Task ClaudeStatFailurePreservesRealBalanceAndReportsPartial()
    {
        using var http=new HttpClient(new FakeHttp(request=>request.RequestUri!.AbsolutePath switch{"/api/user/self"=>Response("""{"success":true,"data":{"quota":500000}}"""),"/api/log/self/stat"=>new(HttpStatusCode.Forbidden),_=>Response("""{"success":true,"data":{"items":[]}}""")}));
        var result=await new ClaudeCNAdapter(http,new(){ClaudeUserId="42"},new TestSecret()).RefreshAsync(true,CancellationToken.None);Assert.Equal(1,result.RemainingBalance);Assert.Null(result.TodayCost);Assert.Null(result.TotalCost);Assert.Equal(ConnectionStatus.Partial,result.ConnectionStatus);Assert.Contains("HTTP 403",result.ErrorMessage);
    }
    [Fact]public async Task ClaudeApiErrorNeverEchoesSecretResponseBody()
    {
        using var http=new HttpClient(new FakeHttp(_=>Response("""{"success":false,"message":"test-only secret token account"}""")));var ex=await Assert.ThrowsAsync<ProviderException>(()=>new ClaudeCNAdapter(http,new(){ClaudeUserId="42"},new TestSecret()).RefreshAsync(true,CancellationToken.None));Assert.DoesNotContain("secret token",ex.Message);
    }
    [Fact]public async Task MissingCredentialProducesUnavailableWithoutNetwork()
    {
        using var http=new HttpClient(new FakeHttp(_=>throw new Exception("Must not call")));var result=await new ClaudeCNAdapter(http,new(),new EmptySecret()).RefreshAsync(true,CancellationToken.None);Assert.Equal(ConnectionStatus.Unavailable,result.ConnectionStatus);Assert.Null(result.RemainingBalance);
    }
    [Fact]public async Task RefreshFailureIsIsolatedAndDisposalCancelsWork()
    {
        await using var coordinator=new RefreshCoordinator(new(),[new FakeAdapter("codex",true),new FakeAdapter("dsh",false)]);await coordinator.RefreshAsync(true);Assert.Equal(ConnectionStatus.Unavailable,coordinator.Snapshots.Single(x=>x.ProviderId=="codex").ConnectionStatus);Assert.Equal(ConnectionStatus.Ready,coordinator.Snapshots.Single(x=>x.ProviderId=="dsh").ConnectionStatus);
    }
    [Fact]public async Task DshChoosesHighestGenerationAndRefreshIsIdempotent()
    {
        var directory=Path.Combine(Path.GetTempPath(),"quota-generation-"+Guid.NewGuid());Directory.CreateDirectory(directory);try{File.WriteAllText(Path.Combine(directory,"session.jsonl"),Rows(new{type="session",version=0,id="same-session"},Context(),Message(2,1000,500)));File.WriteAllText(Path.Combine(directory,"session.v4.jsonl"),Rows(Header("same-session"),Context(),Message(2,100,50)));var adapter=new DshAdapter(new(){DshSessionRoot=directory});var first=await adapter.RefreshAsync(true,CancellationToken.None);var second=await adapter.RefreshAsync(true,CancellationToken.None);Assert.Equal(150,first.TotalTokens);Assert.Equal(first.TotalTokens,second.TotalTokens);}finally{Directory.Delete(directory,true);}
    }
    [Fact]public async Task ClaudeCacheRespectsMidnightAndConversionChanges()
    {
        var calls=0;var time=new ManualTime(new DateTimeOffset(2026,10,10,12,0,0,TimeSpan.FromHours(8)));
        using var http=new HttpClient(new FakeHttp(r=>{calls++;return Response(r.RequestUri!.AbsolutePath switch{"/api/user/self"=>"""{"success":true,"data":{"quota":500000,"used_quota":2}}""","/api/log/self/stat"=>"""{"success":true,"data":{"quota":2,"total_tokens":6,"total_requests":1}}""",_=>"""{"success":true,"data":{"items":[]}}"""});}));
        var settings=new MonitorSettings{ClaudeUserId="42"};var adapter=new ClaudeCNAdapter(http,settings,new TestSecret(),time);await adapter.RefreshAsync(false,CancellationToken.None);Assert.Equal(3,calls);time.Now=time.Now.AddSeconds(180);await adapter.RefreshAsync(false,CancellationToken.None);Assert.Equal(4,calls);settings.QuotaPerUsd=1000000;var changed=await adapter.RefreshAsync(false,CancellationToken.None);Assert.Equal(7,calls);Assert.Equal(0.000002m,changed.TodayCost);time.Now=time.Now.AddDays(1);await adapter.RefreshAsync(false,CancellationToken.None);Assert.Equal(10,calls);
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
