using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Tests;
public sealed class MorphTests
{
    [Fact] public void MotionStaysBoundedAndNeverRebounds()
    {
        var motion=new MorphMotion(176);motion.Retarget(520,0,.32);var previous=176d;
        for(var i=1;i<=320;i++){var width=motion.Sample(i/1000d);Assert.InRange(width,previous,520);previous=width;}
        Assert.Equal(520,motion.Value);Assert.False(motion.Active);
    }
    [Fact] public void RapidReversalSamplesCurrentPositionAndCannotSnap()
    {
        var motion=new MorphMotion(176);motion.Retarget(520,0,.32);
        var width=motion.Sample(.12);motion.Retarget(176,.12,.32);Assert.Equal(width,motion.Value);
        var prior=width;for(var i=1;i<=100;i++){var value=motion.Sample(.12+i/1000d);Assert.InRange(value,176,prior);prior=value;}
        width=motion.Value;motion.Retarget(520,.22,.32);Assert.Equal(width,motion.Value);Assert.InRange(motion.Sample(.23),width,520);
    }
    [Fact] public void SameTargetRefreshDoesNotRestartTheCurve()
    {
        var motion=new MorphMotion(176);motion.Retarget(520,0,.32);motion.Sample(.12);motion.Retarget(520,.12,.32);Assert.Equal(520,motion.Sample(.32));
    }
    [Fact] public void AllAlertCombinationsHaveOneSlotPerHarnessAndRealNumbers()
    {
        var now=DateTimeOffset.UtcNow;var settings=new MonitorSettings();
        for(var mask=0;mask<16;mask++)
        {
            ProviderSnapshot[] providers=[new(){ProviderId="codex",DisplayName="Codex",LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,Windows=[new("codex","Primary",(mask&1)>0?88:10,300,now.AddHours(1)),new("codex","Secondary",(mask&2)>0?92:10,10080,now.AddDays(1))]},new(){ProviderId="claude",DisplayName="Claude",LastUpdated=now,ConnectionStatus=ConnectionStatus.Partial,Currency="USD",RemainingBalance=(mask&4)>0?.52m:10},new(){ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY",(mask&8)>0?".35":"10"),new("CNY",".45")],[],now,"fixture"),DshBalanceState.Ready,null)}];
            var slots=LowQuotaAlerts.Minimal(LowQuotaAlerts.Evaluate(providers,settings,now));
            Assert.Equal(((mask&3)>0?1:0)+((mask&4)>0?1:0)+((mask&8)>0?1:0),slots.Count);
            if((mask&3)>0)Assert.Equal((mask&2)>0?"7d 8%":"5h 12%",slots[0].Value);
            if((mask&4)>0)Assert.Equal("$0.52",slots.Single(a=>a.Key=="claude").Value);
            if((mask&8)>0)Assert.Equal("¥0.80",slots.Single(a=>a.Key=="dsh").Value);
        }
    }
}
