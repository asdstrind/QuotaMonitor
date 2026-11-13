using System.Net;
using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;

namespace QuotaMonitor.Tests;

public sealed class OpenCodeGoTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 0, 30, 0, TimeSpan.Zero);
    private const string Header = "day,user_type,user_id,user_name,provider,model,requests,input_tokens,output_tokens,cache_read_tokens,cache_write_5m_tokens,cache_write_1h_tokens,cost_micro_cents,cost_usd\r\n";
    private static string Row(string day="2026-11-11",string provider="opencode-go",string model="model-a",string user="principal-a",string input="100",string output="20",string cache="500",string write5="7",string write1="9",string requests="2") => $"{day},member,{user},\"ignored, identity\",{provider},{model},{requests},{input},{output},{cache},{write5},{write1},0,0\r\n";
    private static string Quota(double percent=93) => JsonSerializer.Serialize(new { usage = new { rolling = new { percent, status="ok", resetsAt=Now.AddHours(2) }, weekly=new { percent, status="rate-limited", resetsAt=Now.AddDays(2) }, monthly=new { percent, status="ok", resetsAt=Now.AddDays(10) } } });
    private static UsageHistory Parse(string body) => OpenCodeGoExport.Parse(body,Now);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = OpenCodeGoTests.Now; public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Secret : ISecretStore { public string? Key="synthetic-readonly-key";public string? Read()=>Key;public void Write(string value)=>Key=value;public void Delete()=>Key=null; }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    { public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(send(request));} }
    private static HttpResponseMessage Response(string body,string type="application/json")=>new(HttpStatusCode.OK){Content=new StringContent(body,System.Text.Encoding.UTF8,type)};

    [Fact] public void DisjointBucketsAndProviderAreStrict()
    {
        var h=Parse(Header+Row()+Row(provider:"opencode",model:"opencode-go",input:"9999999999"));
        var d=Assert.Single(h.Days);Assert.Equal(636,d.Tokens);Assert.Equal(500,d.CachedTokens);Assert.Equal(2,d.Requests);
        Assert.Null(h.Account);Assert.DoesNotContain("principal-a",JsonSerializer.Serialize(h));Assert.DoesNotContain("ignored",JsonSerializer.Serialize(h));
    }
    [Fact] public void ModelsPrincipalsAndDatesAggregateWithoutDuplication()
    {
        var h=Parse(Header+Row()+Row()+Row(model:"model-b")+Row(user:"principal-b")+Row(day:"2026-11-10"));
        Assert.Equal(1908,h.Days.Single(d=>d.Date==new DateOnly(2026,11,11)).Tokens);Assert.Equal(636,h.Days.Single(d=>d.Date==new DateOnly(2026,11,10)).Tokens);
        Assert.Equal(2544,UsageGrid.Build([h],new(2026,11,11)).Providers.Single(p=>p.Id=="opencode-go").Cumulative.Tokens);
    }
    [Fact] public void ContradictoryDuplicateIsUnknown()
    {var d=Assert.Single(Parse(Header+Row()+Row(input:"101")).Days);Assert.Null(d.Tokens);Assert.Equal(UsageQuality.Partial,d.Quality);Assert.Equal(1,d.Excluded);}
    [Theory][InlineData("")][InlineData("  \r\n")][InlineData(Header)]
    public void EmptyExportsDoNotInventZeroDays(string csv){var h=Parse(csv);Assert.Empty(h.Days);Assert.Null(UsageGrid.Build([h],new(2026,11,11)).Providers.Single(p=>p.Id=="opencode-go").Cumulative.Tokens);}
    [Fact] public void ExplicitNumericZeroRemainsKnownZero(){Assert.Equal(0,Assert.Single(Parse(Header+Row(input:"0",output:"0",cache:"0",write5:"0",write1:"0",requests:"0")).Days).Tokens);}
    [Fact] public void MissingAndMalformedFieldsKeepUnknown()
    {
        Assert.Throws<ProviderException>(()=>Parse("unexpected,header\n1,2"));
        var h=Parse("day,provider,user_type,user_id,model,requests,input_tokens,output_tokens\n2026-11-11,opencode-go,member,p,m,1,2,3");
        Assert.Null(Assert.Single(h.Days).Tokens);Assert.NotNull(h.Error);
    }
    [Theory][InlineData("-1")][InlineData("1.5")][InlineData("9223372036854775808")][InlineData("secret text")][InlineData("")]
    public void InvalidIntegersNeverBecomeZero(string value){var h=Parse(Header+Row(input:value));Assert.Null(Assert.Single(h.Days).Tokens);Assert.NotNull(h.Error);}
    [Fact] public void BillionPlusIsExactAndOverflowIsUnknown()
    {
        var h=Parse(Header+Row(input:"1500000001"));Assert.Equal(1500000537,Assert.Single(h.Days).Tokens);
        Assert.Null(Assert.Single(Parse(Header+Row(input:long.MaxValue.ToString())).Days).Tokens);
        var grid=UsageGrid.Build([Parse(Header+Row(input:"0",output:"0",cache:long.MaxValue.ToString(),write5:"0",write1:"0")+Row(day:"2026-11-10"))],new(2026,11,11));
        Assert.Null(grid.Providers.Single(p=>p.Id=="opencode-go").Cumulative.Tokens);
    }
    [Fact] public void UtcDatesStayUtcAndUnknownEarlierDatesStayMissing()
    {
        var h=OpenCodeGoExport.Parse(Header+Row()+Row(day:"2026-08-13")+Row(day:"2026-08-12")+Row(day:"2026-11-12"),Now.ToOffset(TimeSpan.FromHours(-8)));
        Assert.Contains(h.Days,d=>d.Date==new DateOnly(2026,11,11));Assert.Contains(h.Days,d=>d.Date==new DateOnly(2026,8,13));Assert.DoesNotContain(h.Days,d=>d.Date==new DateOnly(2026,8,12));
        var grid=UsageGrid.Build([h],new(2026,11,11));Assert.Equal(LaneState.Missing,grid.Days.First().Lanes.Single(l=>l.ProviderId=="opencode-go").State);
    }
    [Fact] public void CsvQuotesBomAndReorderedHeaders()
    {
        var h=Parse("\uFEFF"+Header+Row(model:"\"quoted, model\""));Assert.Equal(636,Assert.Single(h.Days).Tokens);
        Assert.Throws<ProviderException>(()=>Parse(Header+"\"unfinished"));Assert.Throws<ProviderException>(()=>Parse("day,day,provider\na,a,b"));
    }
    [Fact] public void PercentStatusAndResetRetainServiceMeaning()
    {
        var s=OpenCodeGoAdapter.ParseQuota(Quota(37.25),Now);Assert.Equal(62.75,s.RemainingPercent);Assert.Equal(3,s.Windows.Count);Assert.Equal("rate-limited",s.Windows.Single(w=>w.Bucket=="weekly").ServerStatus);Assert.Equal(Now.AddHours(2),s.Windows.First().ResetAt);
        var vm=new ProviderViewModel(s);Assert.True(vm.IsQuota);Assert.Equal(new[]{"5 小时","每周","每月"},vm.Gauges.Select(g=>g.Label));
    }
    [Theory][InlineData("{}")][InlineData("<html>private body</html>")][InlineData("{\"usage\":{\"rolling\":{\"percent\":\"bad\"}}}")]
    public void UnusableQuotaSchemaNeverDefaultsToZero(string json){Assert.ThrowsAny<Exception>(()=>OpenCodeGoAdapter.ParseQuota(json,Now));}
    [Theory][InlineData(401)][InlineData(403)][InlineData(429)][InlineData(500)][InlineData(503)]
    public async Task HttpFailuresRemainUnavailableAndDoNotEchoBodies(int code)
    {
        using var http=new HttpClient(new Handler(_=>new((HttpStatusCode)code){Content=new StringContent("private account data")}));var adapter=new OpenCodeGoAdapter(http,new(),new Secret());
        var s=await adapter.RefreshAsync(true,default);Assert.Null(s.RemainingPercent);Assert.Empty(s.Windows);Assert.Equal(ConnectionStatus.Unavailable,s.ConnectionStatus);Assert.DoesNotContain("private",s.ErrorMessage);
    }
    [Fact] public async Task EndpointsShareIndependentCredentialAndConservativeCooldowns()
    {
        var clock=new Clock();var handler=new Handler(r=>{Assert.Equal("Bearer",r.Headers.Authorization?.Scheme);Assert.Equal("synthetic-readonly-key",r.Headers.Authorization?.Parameter);Assert.False(r.Headers.Contains("Cookie"));Assert.Contains("QuotaMonitor",r.Headers.UserAgent.ToString());return r.RequestUri!.AbsolutePath.EndsWith("export")?Response(Header+Row(),"text/csv"):Response(Quota());});
        using var http=new HttpClient(handler);var a=new OpenCodeGoAdapter(http,new(),new Secret(),clock);
        await a.RefreshAsync(true,default);var h=await a.ReadHistoryAsync(null,new(new(2026,11,11),DateOnly.MinValue,true),default);
        await a.RefreshAsync(true,default);await a.ReadHistoryAsync(h,new(new(2026,11,11),DateOnly.MinValue,true),default);Assert.Equal(2,handler.Calls);
        clock.Now=clock.Now.AddMinutes(3);await a.RefreshAsync(false,default);Assert.Equal(3,handler.Calls);
    }
    [Fact] public async Task RetryAfterAppliesEvenToForceAndStaleQuotaKeepsRealValue()
    {
        var clock=new Clock();var handler=new Handler(_=>Response(Quota()));using var http=new HttpClient(handler);var a=new OpenCodeGoAdapter(http,new(),new Secret(),clock);var first=await a.RefreshAsync(true,default);
        using var failHttp=new HttpClient(new Handler(_=>{var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);r.Headers.RetryAfter=new(TimeSpan.FromHours(2));return r;}));
        var failHandler=new Handler(_=>{if(clock.Now==Now)return Response(Quota());var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);r.Headers.RetryAfter=new(TimeSpan.FromHours(2));return r;});
        using var client=new HttpClient(failHandler);var adapter=new OpenCodeGoAdapter(client,new(),new Secret(),clock);await adapter.RefreshAsync(true,default);clock.Now=clock.Now.AddMinutes(3);
        var failed=await adapter.RefreshAsync(true,default);Assert.Equal(ConnectionStatus.Stale,failed.ConnectionStatus);Assert.Equal(first.RemainingPercent,failed.RemainingPercent);clock.Now=clock.Now.AddHours(1);await adapter.RefreshAsync(true,default);Assert.Equal(2,failHandler.Calls);
    }
    [Fact] public async Task KeyChangeAndRemovalClearPreviousAccountData()
    {
        var secret=new Secret();var handler=new Handler(_=>Response(Quota()));using var http=new HttpClient(handler);var a=new OpenCodeGoAdapter(http,new(),secret);
        await a.RefreshAsync(true,default);secret.Key=null;var empty=await a.RefreshAsync(true,default);Assert.True(empty.NeedsSetup);Assert.Null(empty.RemainingPercent);Assert.Empty(empty.Windows);Assert.Equal(1,handler.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task NetworkAndTimeoutAreSafe(bool timeout)
    {
        using var http=new HttpClient(new Handler(_=>timeout?throw new TaskCanceledException():throw new HttpRequestException("secret diagnostic")));var a=new OpenCodeGoAdapter(http,new(),new Secret());var s=await a.RefreshAsync(true,default);Assert.Null(s.RemainingPercent);Assert.DoesNotContain("secret",s.ErrorMessage);
    }
    [Fact] public void ThreeIndependentAlertsUseOneMinimalIconAndRespectStrictThreshold()
    {
        var s=OpenCodeGoAdapter.ParseQuota(Quota(),Now);var alerts=LowQuotaAlerts.Evaluate([s],new(),Now);Assert.Equal(3,alerts.Count);Assert.Single(LowQuotaAlerts.Minimal(alerts));Assert.Equal("go-month",LowQuotaAlerts.Minimal(alerts)[0].Key);
        Assert.Empty(LowQuotaAlerts.Evaluate([s],new(){GoFiveHourLowPercent=7,GoWeeklyLowPercent=7,GoMonthlyLowPercent=7},Now));
        Assert.Empty(LowQuotaAlerts.Evaluate([s with{ConnectionStatus=ConnectionStatus.Stale}],new(),Now));
    }
    [Fact] public void OldSettingsCannotMapOldProviderFieldsToGo()
    {
        var s=JsonSerializer.Deserialize<MonitorSettings>("""{"ClaudeEnabled":false,"ShowClaude":false,"ClaudeUserId":"synthetic-old-id","ClaudeLowBalanceUsd":999,"ClaudeRefreshSeconds":34,"CodexEnabled":false,"DshRefreshSeconds":70,"AntigravityClaudeGptLowPercent":21,"Left":432}""")!;
        Assert.True(s.GoEnabled&&s.ShowGo);Assert.Equal(180,s.GoRefreshSeconds);Assert.Equal(15,s.GoFiveHourLowPercent);Assert.False(s.CodexEnabled);Assert.Equal(70,s.DshRefreshSeconds);Assert.Equal(21,s.AntigravityClaudeGptLowPercent);Assert.Equal(432,s.Left);Assert.DoesNotContain("ClaudeUserId",JsonSerializer.Serialize(s));
    }
    [Fact] public void RealStoreMigrationPreservesOtherSettingsAndLegacyHistory()
    {
        var root=Path.Combine(Path.GetTempPath(),"qm-go-migration-"+Guid.NewGuid());
        try
        {
            var store=new QuotaMonitor.Configuration.SettingsStore(root);
            var original="""{"ClaudeEnabled":false,"ShowClaude":false,"ClaudeUserId":"synthetic-legacy","CodexEnabled":false,"DshSessionRoot":"synthetic-path","DshRefreshSeconds":71,"AntigravityClaudeGptLowPercent":23,"TopCenter":false,"Left":321,"Top":123}""";
            File.WriteAllText(Path.Combine(root,"settings.json"),original);
            store.SaveHistory([new(){ProviderId="claude",Days=[new(new(2026,9,1),123,1,null,UsageQuality.Exact)]}]);
            var before=File.ReadAllBytes(Path.Combine(root,"history.json"));var migrated=store.Load();store.Save(migrated);
            Assert.Equal(original,File.ReadAllText(Path.Combine(root,"settings.json.bak")));Assert.Equal(before,File.ReadAllBytes(Path.Combine(root,"history.json")));
            Assert.False(migrated.CodexEnabled);Assert.Equal("synthetic-path",migrated.DshSessionRoot);Assert.Equal(71,migrated.DshRefreshSeconds);Assert.Equal(23,migrated.AntigravityClaudeGptLowPercent);Assert.Equal(123,migrated.Top);Assert.True(migrated.GoEnabled);
            var vm=new MonitorViewModel();foreach(var h in store.LoadHistory())vm.UpdateHistory(h,new(2026,11,11));Assert.Single(vm.Histories);Assert.DoesNotContain(vm.Lanes,l=>l.Id=="claude");Assert.All(vm.Lanes,l=>Assert.Equal("—",l.Total));
            store.SaveHistory(vm.Histories);Assert.Equal("claude",Assert.Single(store.LoadHistory()).ProviderId);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public void AllEightGoAlertWindowCombinationsRemainOneIcon()
    {
        for(var mask=0;mask<8;mask++)
        {
            var s=OpenCodeGoAdapter.ParseQuota(Quota(0),Now);s=s with{Windows=s.Windows.Select((w,i)=>w with{UsedPercent=(mask&(1<<i))!=0?97:20}).ToArray()};
            var alerts=LowQuotaAlerts.Evaluate([s],new(),Now);Assert.Equal(System.Numerics.BitOperations.PopCount((uint)mask),alerts.Count);Assert.Equal(mask==0?0:1,LowQuotaAlerts.Minimal(alerts).Count);
        }
    }
    [Fact] public async Task HistoryErrorKeepsVerifiedDaysAndHonorsRetryAfter()
    {
        var clock=new Clock();var handler=new Handler(_=>clock.Now==Now?Response(Header+Row(),"text/csv"):new(HttpStatusCode.ServiceUnavailable){Headers={RetryAfter=new(TimeSpan.FromHours(3))},Content=new StringContent("private identity")});
        using var http=new HttpClient(handler);var adapter=new OpenCodeGoAdapter(http,new(),new Secret(),clock);var request=new HistoryRequest(new(2026,11,11),DateOnly.MinValue,true);
        var first=await adapter.ReadHistoryAsync(null,request,default);clock.Now=clock.Now.AddMinutes(30);var failed=await adapter.ReadHistoryAsync(first,request,default);
        Assert.Equal(first.Days,failed.Days);Assert.NotNull(failed.Error);Assert.DoesNotContain("private",failed.Error);clock.Now=clock.Now.AddHours(1);await adapter.ReadHistoryAsync(failed,request,default);Assert.Equal(2,handler.Calls);
    }
    [Fact] public async Task JsonShapeChangeKeepsLastQuotaAndInvalidUtf8IsSafe()
    {
        var clock=new Clock();var handler=new Handler(_=>Response(clock.Now==Now?Quota():"[]"));using var http=new HttpClient(handler);var a=new OpenCodeGoAdapter(http,new(),new Secret(),clock);var first=await a.RefreshAsync(true,default);clock.Now=clock.Now.AddMinutes(3);var stale=await a.RefreshAsync(true,default);Assert.Equal(ConnectionStatus.Stale,stale.ConnectionStatus);Assert.Equal(first.Windows,stale.Windows);
        using var bad=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent([0xff,0xfe,0xff])}));var result=await new OpenCodeGoAdapter(bad,new(),new Secret()).RefreshAsync(true,default);Assert.Equal(ConnectionStatus.Unavailable,result.ConnectionStatus);Assert.Null(result.RemainingPercent);
    }
    [Fact] public void GoAlertsUseCompactReleaseUnitsForEveryWindow()
    {
        var alerts=LowQuotaAlerts.Evaluate([OpenCodeGoAdapter.ParseQuota(Quota(),Now)],new(),Now);
        Assert.Equal(new[]{"5h","7d","1mo"},alerts.Select(a=>a.Label));
        Assert.Equal(new[]{"5h 7%","7d 7%","1mo 7%"},alerts.Select(a=>a.Value));
        Assert.All(alerts,a=>Assert.Equal("#A6A6A8",a.Color));
        foreach(var (key,name) in new[]{("go-5h","5 小时"),("go-week","每周"),("go-month","每月")})
        {
            var a=alerts.Single(x=>x.Key==key);
            Assert.Contains("OpenCode Go",a.Description);Assert.Contains(name,a.Description);
            Assert.Contains("剩余 7%",a.Description);Assert.Contains("低于 15%",a.Description);
        }
    }
    [Fact] public void MonthlyOnlyGoAlertUses1moInOneMinimalSlot()
    {
        var s=OpenCodeGoAdapter.ParseQuota(Quota(0),Now);s=s with{Windows=s.Windows.Select(w=>w with{UsedPercent=w.Bucket=="monthly"?97:20}).ToArray()};
        var alerts=LowQuotaAlerts.Evaluate([s],new(),Now);var alert=Assert.Single(alerts);
        Assert.Equal("go-month",alert.Key);Assert.Equal("1mo",alert.Label);Assert.Equal("1mo 3%",alert.Value);Assert.Contains("每月",alert.Description);
        var minimal=Assert.Single(LowQuotaAlerts.Minimal(alerts));
        Assert.Equal("go-month",minimal.Key);Assert.Equal("1mo",minimal.Label);Assert.Equal("1mo 3%",minimal.Value);
        Assert.Empty(LowQuotaAlerts.Evaluate([s],new(){GoMonthlyLowPercent=3},Now));
        Assert.Empty(LowQuotaAlerts.Evaluate([s with{ConnectionStatus=ConnectionStatus.Stale}],new(),Now));
    }
    [Fact] public void AllThreeGoWindowsStayIndependentAndMonthlyWinsMinimalSlot()
    {
        var alerts=LowQuotaAlerts.Evaluate([OpenCodeGoAdapter.ParseQuota(Quota(),Now)],new(),Now);
        Assert.Equal(new[]{"go-5h","go-week","go-month"},alerts.Select(a=>a.Key));
        Assert.Equal(3,alerts.Select(a=>a.Key).Distinct().Count());
        var minimal=LowQuotaAlerts.Minimal(alerts);
        Assert.Single(minimal,a=>a.Key.StartsWith("go-",StringComparison.Ordinal));
        Assert.Equal("go-month",Assert.Single(minimal).Key);Assert.Equal("1mo",minimal[0].Label);Assert.Equal("1mo 7%",minimal[0].Value);
    }
    [Fact] public void OtherHarnessCompactLabelsStayUnchanged()
    {
        // AlertReadout keeps its own compact literals: codex-week shows "7d", dsh shows CNY/USD from the wallet symbol. These assertions pin the unchanged core inputs.
        var codex=new ProviderSnapshot{ProviderId="codex",DisplayName="Codex",ConnectionStatus=ConnectionStatus.Ready,LastUpdated=Now,Windows=[new("codex","Primary",90,300,Now.AddHours(1)),new("codex","Secondary",90,10080,Now.AddDays(1))]};
        var c=LowQuotaAlerts.Evaluate([codex],new(),Now).ToDictionary(a=>a.Key);
        Assert.Equal("5h",c["codex-5h"].Label);Assert.Equal("5h 10%",c["codex-5h"].Value);Assert.Equal("#19A67A",c["codex-5h"].Color);
        Assert.Equal("W",c["codex-week"].Label);Assert.Equal("7d 10%",c["codex-week"].Value);Assert.Equal("#19A67A",c["codex-week"].Color);
        var cny=new ProviderSnapshot{ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY","4")],[new("CNY","100")],Now,"test"),DshBalanceState.Ready,null)};
        var cnyAlert=Assert.Single(LowQuotaAlerts.Evaluate([cny],new(),Now));
        Assert.Equal("dsh",cnyAlert.Key);Assert.Equal("D",cnyAlert.Label);Assert.Equal("#4A8AF0",cnyAlert.Color);
        Assert.Equal("¥4.00",cnyAlert.Value);Assert.Contains("低于 CNY 5",cnyAlert.Description);
        var usd=cny with{AccountBalance=new(new([new("USD","1")],[],Now,"test"),DshBalanceState.Ready,null)};
        Assert.Equal("$1.00",Assert.Single(LowQuotaAlerts.Evaluate([usd],new(){DshAlertCurrency="USD"},Now)).Value);
    }
}
