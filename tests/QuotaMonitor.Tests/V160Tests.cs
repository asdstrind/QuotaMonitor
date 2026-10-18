using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;

namespace QuotaMonitor.Tests;

public sealed class V160Tests
{
    private static readonly DateTimeOffset Now = new(2026,10,17,3,0,0,TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026,10,17);
    private static string Payload(object[] buckets) => JsonSerializer.Serialize(new { response = new { groups = new[] { new { displayName = "quota", buckets } } } });
    private static object Bucket(string id, double remaining, string window="weekly", bool disabled=false) => new { bucketId=id, remainingFraction=remaining, window, disabled, resetTime=Now.AddDays(7).ToString("o") };
    private static ProviderSnapshot Quota(double gemini=.1,double third=.09) => AntigravityAdapter.Parse(Payload([Bucket("gemini-weekly",gemini),Bucket("3p-weekly",third)]),Now);
    private static UsageHistory History(string id, params UsageDay[] days) => new() { ProviderId=id,Days=days };
    private static UsageDay Day(long? tokens, int offset=0, UsageQuality quality=UsageQuality.Exact) => new(Today.AddDays(offset),tokens,1,null,quality,Now);
    private sealed class Clock : TimeProvider { public DateTimeOffset Current=Now; public override DateTimeOffset GetUtcNow()=>Current; }

    [Fact] public void WeeklyIdsRemainIndependentAndNeverUseFiveHourNumbers()
    {
        var s=AntigravityAdapter.Parse(Payload([Bucket("3p-5h",0,"five-hour"),Bucket("gemini-weekly",.67),Bucket("3p-weekly",.23),Bucket("gemini-5h",.01,"five-hour")]),Now);
        Assert.Equal(ConnectionStatus.Ready,s.ConnectionStatus);Assert.Equal(2,s.Windows.Count);
        Assert.Equal(67,s.Windows[0].RemainingPercent,5);Assert.Equal(23,s.Windows[1].RemainingPercent,5);
        Assert.All(s.Windows,w=>{Assert.Equal(10080,w.DurationMinutes);Assert.Equal(Now.AddDays(7),w.ResetAt);});
        Assert.Equal(Now,s.LastUpdated);Assert.Null(s.TodayTokens);Assert.Null(s.UsageUpdated);
    }
    [Theory][InlineData("{}")][InlineData("{\"response\":{\"groups\":[]}}")][InlineData("{\"code\":\"UNAUTHENTICATED\"}")]
    public void NoQuotaResponseNeverBecomesZeroOrFull(string json)
    {
        var s=AntigravityAdapter.Parse(json,Now);Assert.Equal(ConnectionStatus.Unavailable,s.ConnectionStatus);Assert.Null(s.RemainingPercent);Assert.Empty(s.Windows);Assert.Null(s.LastUpdated);
    }
    [Fact] public void MissingDisabledDuplicateAndOutOfRangeBucketsAreUnknown()
    {
        foreach(var invalid in new[]{Bucket("gemini-weekly",.1,"five-hour"),Bucket("gemini-weekly",-1),Bucket("gemini-weekly",1.1),Bucket("gemini-weekly",.1,disabled:true)})
        {
            var s=AntigravityAdapter.Parse(Payload([invalid,Bucket("3p-weekly",.3)]),Now);
            Assert.Equal(ConnectionStatus.Partial,s.ConnectionStatus);Assert.Single(s.Windows);Assert.Equal("3p-weekly",s.Windows[0].Bucket);
        }
        Assert.Empty(AntigravityAdapter.Parse(Payload([Bucket("gemini-weekly",.1),Bucket("gemini-weekly",.2)]),Now).Windows);
        var missingFraction=AntigravityAdapter.Parse(Payload([new {bucketId="gemini-weekly",window="weekly"}]),Now);
        Assert.Empty(missingFraction.Windows);
    }
    [Fact] public void NestedExplicitZeroIsRealAndMissingResetIsUnknown()
    {
        var s=AntigravityAdapter.Parse(Payload([new {bucketId="gemini-weekly",window="weekly",remaining=new { @case="remainingFraction",value=0d }}]),Now);
        Assert.Equal(0,s.Windows[0].RemainingPercent);Assert.Null(s.Windows[0].ResetAt);
    }
    [Fact] public async Task FailureRetainsStaleTimeButAuthenticationClearsPreviousAccount()
    {
        var call=0;var clock=new Clock();
        var adapter=new AntigravityAdapter(_=>++call switch {
            1=>Task.FromResult(Payload([Bucket("gemini-weekly",.67),Bucket("3p-weekly",.23)])),
            2=>throw new AntigravityReadException("disconnected"),
            _=>throw new AntigravityReadException("signed out",true)},clock);
        var first=await adapter.RefreshAsync(false,default);clock.Current=Now.AddMinutes(1);
        var stale=await adapter.RefreshAsync(false,default);Assert.Equal(ConnectionStatus.Stale,stale.ConnectionStatus);Assert.Equal(first.LastUpdated,stale.LastUpdated);Assert.Equal(first.Windows,stale.Windows);
        Assert.Empty(LowQuotaAlerts.Evaluate([stale],new(),clock.Current));
        var signedOut=await adapter.RefreshAsync(false,default);Assert.Equal(ConnectionStatus.Unavailable,signedOut.ConnectionStatus);Assert.Empty(signedOut.Windows);Assert.Null(signedOut.LastUpdated);
    }
    [Theory][InlineData(.5,.5,0)][InlineData(.1,.5,1)][InlineData(.5,.1,1)][InlineData(.1,.1,2)]
    public void SingleAndDualWeeklyAlertsUseOneHarnessIcon(double gemini,double third,int decisions)
    {
        var alerts=LowQuotaAlerts.Evaluate([Quota(gemini,third)],new(),Now);Assert.Equal(decisions,alerts.Count);
        var minimal=LowQuotaAlerts.Minimal(alerts);Assert.Equal(decisions==0?0:1,minimal.Count);
        if(decisions==2){Assert.Equal("antigravity",minimal[0].Key);Assert.Equal("Gem",minimal[0].Label);Assert.Equal("C/G",minimal[0].SecondLabel);Assert.Equal("10%",minimal[0].SecondValue);}
        if(decisions==1)Assert.Null(minimal[0].SecondValue);
    }
    [Fact] public void AntigravityAlertRequiresFreshVisibleRealWeeklyBelowStrictThreshold()
    {
        Assert.Empty(LowQuotaAlerts.Evaluate([Quota(.15,.15)],new(),Now));
        Assert.Empty(LowQuotaAlerts.Evaluate([Quota()],new(){ShowAntigravity=false},Now));
        Assert.Empty(LowQuotaAlerts.Evaluate([Quota()],new(),Now.AddMinutes(4)));
        Assert.Empty(LowQuotaAlerts.Evaluate([Quota()],new(){AntigravityGeminiLowPercent=0,AntigravityClaudeGptLowPercent=0},Now));
        Assert.Empty(LowQuotaAlerts.Evaluate([Quota() with {IsEstimated=true}],new(),Now));
    }
    [Fact] public void EveryVisibilityCombinationFiltersAllCalculationsAndRetainsHistory()
    {
        var vm=new MonitorViewModel();
        foreach(var id in UsageGrid.Order){vm.Providers.Add(new(ProviderSnapshot.Empty(id,MonitorViewModel.NameOf(id),MetricType.Tokens)));vm.UpdateHistory(History(id,Day(id=="dsh"?350000:100000)),Today);}
        var original=JsonSerializer.Serialize(vm.Histories);
        for(var mask=0;mask<16;mask++)
        {
            var s=new MonitorSettings{ShowCodex=(mask&1)>0,ShowClaude=(mask&2)>0,ShowDsh=(mask&4)>0,ShowAntigravity=(mask&8)>0};
            vm.HoverLane="codex";vm.ApplyVisibility(s,Today);
            var ids=UsageGrid.Order.Where(s.Visible).ToArray();Assert.Equal(ids,vm.VisibleProviders.Select(p=>p.Id));Assert.Equal(ids,vm.Grid!.Providers.Select(p=>p.Id));Assert.Equal(ids,vm.Lanes.Select(p=>p.Id));
            var day=vm.Grid.Days[^1];Assert.Equal(ids,day.Lanes.Select(l=>l.ProviderId));
            Assert.Equal(ids.Select(MonitorViewModel.NameOf),vm.BuildTip(day).Rows.Select(r=>r.Name));
            Assert.Equal(ids.Length==0,vm.NoVisibleProviders);
            if(ids.Contains("antigravity"))Assert.NotNull(vm.Grid.Providers.First(p=>p.Id=="antigravity").History);
            var expected=ids.Sum(id=>id=="dsh"?350000L:100000L);
            Assert.Equal(expected==0?(long?)null:expected,day.Total);
            Assert.Equal(original,JsonSerializer.Serialize(vm.Histories));
        }
        vm.ApplyVisibility(new(),Today);Assert.Equal(4,vm.VisibleProviders.Count);Assert.Equal(4,vm.Histories.Count);
    }
    [Fact] public void DominantUsesAbsoluteTokensWhileIntensityUsesPooledDailyTotals()
    {
        var grid=UsageGrid.Build([History("codex",Day(200000),Day(900000,-1)),History("claude",Day(100000)),History("dsh",Day(350000))],Today);
        var day=grid.Days[^1];Assert.Equal(650000,day.Total);Assert.Equal("dsh",day.Dominant!.ProviderId);Assert.Equal(900000,grid.SharedReference);
        var filtered=UsageGrid.Build([History("codex",Day(200000)),History("dsh",Day(350000))],Today,visible:["codex"]);
        Assert.Equal(200000,filtered.Days[^1].Total);Assert.Equal("codex",filtered.Days[^1].Dominant!.ProviderId);Assert.Equal(200000,filtered.SharedReference);
    }
    [Fact] public void DuplicateCachesRefreshesAndConflictsCannotDoubleCount()
    {
        var day=Day(2000);var grid=UsageGrid.Build([History("codex",day,day,day)],Today,visible:["codex"]);
        Assert.Equal(2000,grid.Days[^1].Total);Assert.Equal(2000,grid.Providers[0].Tokens);
        var refresh=UsageGrid.Build([History("codex",day),History("codex",day,day)],Today,visible:["codex"]);Assert.Equal(2000,refresh.Days[^1].Total);
        var conflict=UsageGrid.Build([History("codex",day,day with{Tokens=3000})],Today,visible:["codex"]);Assert.Null(conflict.Days[^1].Total);Assert.Equal(LaneState.Missing,conflict.Days[^1].Lanes[0].State);Assert.True(conflict.Days[^1].Incomplete);
    }
    [Fact] public void PartialZeroAndMissingRemainDifferentAndSingleHoverReturnsToCombined()
    {
        var vm=new MonitorViewModel();vm.ApplyVisibility(new(){ShowAntigravity=false},Today);
        vm.UpdateHistory(History("codex",Day(10),Day(0,-1)),Today);vm.UpdateHistory(History("dsh",Day(100,quality:UsageQuality.Partial)),Today);
        var original=JsonSerializer.Serialize(vm.Grid);vm.HoverLane="codex";var tip=vm.BuildTip(vm.Grid!.Days[^1]);Assert.Equal(3,tip.Rows.Count);Assert.Equal("10",tip.Rows[0].Value);Assert.Contains("合计 110",tip.Note);
        vm.HoverLane=null;Assert.Null(vm.FocusLane);Assert.Equal(3,vm.BuildTip(vm.Grid.Days[^1]).Rows.Count);Assert.Equal(original,JsonSerializer.Serialize(vm.Grid));
        var zero=vm.Grid.Days[^2].Lanes[0];Assert.Equal(LaneState.Zero,zero.State);Assert.Equal(0,zero.Tokens);Assert.Equal(LaneState.Missing,vm.Grid.Days[^3].Lanes[0].State);
    }
    [Fact] public void OutlierDoesNotEraseTypicalIntensityAndThemesHaveComparableLightness()
    {
        var values=Enumerable.Repeat(100000L,100).Append(1000000000000L).ToArray();Assert.Equal(100000,UsageGrid.Percentile95(values));
        Assert.True(UsageGrid.Level(10000,UsageGrid.Percentile95(values)!.Value,3)>.6);
        Assert.Equal(0,UsageGrid.TileLevel(0,100000));Assert.Equal(1,UsageGrid.TileLevel(1000000000000L,100000));
        Assert.True(UsageGrid.TileLevel(10000,100000)<UsageGrid.TileLevel(50000,100000));
        static double Light((byte R,byte G,byte B) c)
        {
            static double Linear(byte n){var v=n/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
            var r=Linear(c.R);var g=Linear(c.G);var b=Linear(c.B);
            var l=Math.Cbrt(.4122214708*r+.5363325363*g+.0514459929*b);var m=Math.Cbrt(.2119034982*r+.6806995451*g+.1073969566*b);var s=Math.Cbrt(.0883024619*r+.2817188376*g+.6299787005*b);
            return .2104542553*l+.793617785*m-.0040720468*s;
        }
        foreach(var level in new[]{.1,.3,.6,1})
        {
            var lights=UsageGrid.Order.Select(id=>Light(HeatmapColor.At(id,level))).ToArray();Assert.True(lights.Max()-lights.Min()<.005);
        }
        foreach(var id in UsageGrid.Order)Assert.True(Light(HeatmapColor.At(id,1))>Light(HeatmapColor.At(id,.1)));
    }
    [Fact] public void LegacySettingsKeepValuesAndDefaultNewFieldsWithoutMigrationLoss()
    {
        var s=JsonSerializer.Deserialize<MonitorSettings>("{\"CodexEnabled\":false,\"HeatmapScale\":\"relative\",\"DshLowBalance\":12.345,\"ClaudeUserId\":\"unchanged\"}")!;
        Assert.False(s.CodexEnabled);Assert.Equal("relative",s.HeatmapScale);Assert.Equal(12.345m,s.DshLowBalance);Assert.Equal("unchanged",s.ClaudeUserId);Assert.True(s.ShowCodex&&s.ShowClaude&&s.ShowDsh&&s.ShowAntigravity);Assert.True(s.AntigravityEnabled);
        s.ShowDsh=false;Assert.False(JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(s))!.ShowDsh);
    }
}
