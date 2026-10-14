using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
namespace QuotaMonitor.Tests;
public sealed class SettingsDshTests
{
    [Fact] public void LegacyJsonKeepsAllFieldsAndOptInEstimateDefault()
    {
        var old=new MonitorSettings{ClaudeUserId="42",QuotaPerUsd=123456,DshPricedModel="custom-model",DshInputPerMillion=.2m,DshPriceBasis="custom verified",DshSessionRoot="custom-sessions",Left=712,Top=38,HeatmapScale="relative",ClaudeUsageSeconds=780,AutoStart=true};
        var text=JsonSerializer.Serialize(old);using var doc=JsonDocument.Parse(text);var legacy=JsonSerializer.Serialize(doc.RootElement.EnumerateObject().Where(p=>p.Name!="DshEstimateEnabled").ToDictionary(p=>p.Name,p=>p.Value));var migrated=JsonSerializer.Deserialize<MonitorSettings>(legacy)!;
        Assert.True(migrated.DshEstimateEnabled);Assert.Equal(JsonSerializer.Serialize(old),JsonSerializer.Serialize(migrated));
    }
    [Fact] public void EmptyPathUsesDefaultWithoutRewritingStoredOverride(){var s=new MonitorSettings{DshSessionRoot=""};Assert.Equal(new MonitorSettings().DshSessionRoot,s.EffectiveDshSessionRoot);Assert.Equal("",s.DshSessionRoot);Assert.DoesNotContain("EffectiveDshSessionRoot",JsonSerializer.Serialize(s));}
    [Fact] public async Task UnknownModelAndDisabledPricesDoNotChangeTokenHistory()
    {
        var root=Path.Combine(Path.GetTempPath(),"quota-dsh-settings-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try{
            var at=DateTimeOffset.UtcNow;var rows=new object[]{new{type="session",version=4,id="own",isSeeded=false},new{type="request/context",data=new{provider="deepseek-account",model="session-real-model"}},new{type="assistant/message",time=at.ToUnixTimeMilliseconds(),data=new{turn=1,step=1,usage=new{inputTokens=100,outputTokens=20,cacheReadTokens=40}}}};
            File.WriteAllText(Path.Combine(root,"session.v4.jsonl"),string.Join("\n",rows.Select(r=>JsonSerializer.Serialize(r))));
            var s=new MonitorSettings{DshSessionRoot=root,DshPricedModel="",DshPriceBasis="",DshInputPerMillion=null,DshCacheReadPerMillion=null,DshOutputPerMillion=null};var adapter=new DshAdapter(s);var live=await adapter.RefreshAsync(true,default);var history=adapter.ReadHistory(DateOnly.FromDateTime(DateTime.Today),TimeZoneInfo.Local,default);
            Assert.Equal("session-real-model",live.CurrentModel);Assert.Equal(160,live.TotalTokens);Assert.Null(live.TodayCost);Assert.Equal(160,history.Days.Sum(d=>d.Tokens));
            s.DshEstimateEnabled=false;s.DshPricedModel="session-real-model";s.DshPriceBasis="verified override";s.DshInputPerMillion=1;s.DshCacheReadPerMillion=1;s.DshOutputPerMillion=2;
            Assert.Null(DshAdapter.Estimate([new(100,20,40,0,at,"session-real-model")],s));Assert.Equal(history.Days,adapter.ReadHistory(DateOnly.FromDateTime(DateTime.Today),TimeZoneInfo.Local,default).Days);
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task BalanceIndependentOfMissingModelsPricesAndSessionDirectory()
    {
        var at=DateTimeOffset.UtcNow;var balance=new DshBalanceReading(new([new("CNY","7.123456789")],[],at,"fixture"),DshBalanceState.Ready,null);
        var result=await new DshAdapter(new(){DshSessionRoot=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()),DshEstimateEnabled=false,DshPricedModel="",DshInputPerMillion=null},new FixedBalance(balance)).RefreshAsync(true,default);
        Assert.Same(balance,result.AccountBalance);Assert.Equal(ConnectionStatus.Partial,result.ConnectionStatus);Assert.Null(result.TodayCost);Assert.Null(result.TodayTokens);
    }
    private sealed class FixedBalance(DshBalanceReading reading):IDshBalanceSource{public Task<DshBalanceReading> ReadAsync(CancellationToken ct)=>Task.FromResult(reading);}
}
