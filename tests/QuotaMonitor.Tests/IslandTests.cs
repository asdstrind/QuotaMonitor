using System.Text.Json;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Tests;

public sealed class IslandTests
{
    private static readonly DateTimeOffset Now=new(2026,10,11,8,0,0,TimeSpan.Zero);
    private static readonly MonitorSettings Settings=new();
    private static ProviderSnapshot Codex=>new(){ProviderId="codex",DisplayName="Codex",ConnectionStatus=ConnectionStatus.Ready,LastUpdated=Now,Windows=[new("codex","Primary",90,300,Now.AddHours(1)),new("codex","Secondary",90,10080,Now.AddDays(1))]};
    private static ProviderSnapshot Claude=>new(){ProviderId="claude",DisplayName="Claude",ConnectionStatus=ConnectionStatus.Ready,LastUpdated=Now,RemainingBalance=4,Currency="USD"};
    private static ProviderSnapshot Dsh(string amount="4",DshBalanceState state=DshBalanceState.Ready,DateTimeOffset? at=null)=>new(){ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY",amount)],[new("CNY","100")],at??Now,"test"),state,null)};
    private static IReadOnlyList<LowQuotaAlert> Alert(ProviderSnapshot snapshot,MonitorSettings? settings=null)=>LowQuotaAlerts.Evaluate([snapshot],settings??Settings,Now);

    [Fact]public void ThreeStatesRequireClickAndLeaveGrace()
    {
        var state=new IslandInteraction();Assert.Equal(IslandState.Minimal,state.State);Assert.False(state.Click());
        Assert.False(state.Pointer(true,TimeSpan.Zero));Assert.False(state.Pointer(true,TimeSpan.FromMilliseconds(159)));
        Assert.True(state.Pointer(true,TimeSpan.FromMilliseconds(160)));Assert.Equal(IslandState.Collapsed,state.State);
        Assert.False(state.Pointer(true,TimeSpan.FromSeconds(10)));Assert.Equal(IslandState.Collapsed,state.State);
        Assert.True(state.Click());Assert.Equal(IslandState.Expanded,state.State);
        Assert.False(state.Pointer(false,TimeSpan.FromSeconds(11)));Assert.False(state.Pointer(false,TimeSpan.FromMilliseconds(11899)));
        Assert.True(state.Pointer(false,TimeSpan.FromMilliseconds(11900)));Assert.Equal(IslandState.Minimal,state.State);
    }
    [Fact]public void BriefApproachAndReentryDoNotFlicker()
    {
        var s=new IslandInteraction();s.Pointer(true,TimeSpan.Zero);s.Pointer(false,TimeSpan.FromMilliseconds(100));
        s.Pointer(true,TimeSpan.FromMilliseconds(120));Assert.False(s.Pointer(true,TimeSpan.FromMilliseconds(200)));
        s.Pointer(true,TimeSpan.FromMilliseconds(280));Assert.Equal(IslandState.Collapsed,s.State);
        s.Pointer(false,TimeSpan.FromSeconds(1));s.Pointer(true,TimeSpan.FromMilliseconds(1699));Assert.Equal(IslandState.Collapsed,s.State);
        s.Pointer(false,TimeSpan.FromSeconds(2));Assert.False(s.Pointer(false,TimeSpan.FromMilliseconds(2600)));
    }
    [Fact]public void EscapeAndOwnedDialogSuspendRestartTheGrace()
    {
        var s=new IslandInteraction();s.Set(IslandState.Expanded);
        s.Pointer(false,TimeSpan.Zero);s.Pointer(false,TimeSpan.FromSeconds(1),true);Assert.Equal(IslandState.Expanded,s.State);
        s.Pointer(false,TimeSpan.FromSeconds(2));Assert.False(s.Pointer(false,TimeSpan.FromMilliseconds(2899)));
        Assert.True(s.Pointer(false,TimeSpan.FromMilliseconds(2900)));Assert.Equal(IslandState.Minimal,s.State);
        s.Set(IslandState.Expanded);s.Escape();Assert.Equal(IslandState.Minimal,s.State);
    }
    [Fact]public void ExpandedReentryCancelsAndRapidEdgesNeverAccumulateLeaveTime()
    {
        var s=new IslandInteraction();s.Set(IslandState.Expanded);
        for(var i=0;i<100;i++)
        {
            var at=TimeSpan.FromMilliseconds(i*1000);
            Assert.False(s.Pointer(false,at));Assert.False(s.Pointer(true,at+TimeSpan.FromMilliseconds(850)));
            Assert.Equal(IslandState.Expanded,s.State);
        }
        Assert.False(s.Pointer(false,TimeSpan.FromSeconds(101)));
        Assert.True(s.Pointer(false,TimeSpan.FromMilliseconds(101900)));Assert.Equal(IslandState.Minimal,s.State);
        Assert.False(s.Click()); // No intermediate Collapsed state on exit.
    }
    [Fact]public void AllFourAlertsKeepTheirIndependentKeys()=>Assert.Equal(new[]{"codex-5h","codex-week","claude","dsh"},LowQuotaAlerts.Evaluate([Codex,Claude,Dsh()],Settings,Now).Select(a=>a.Key));
    [Theory][InlineData(ConnectionStatus.Loading)][InlineData(ConnectionStatus.Disabled)][InlineData(ConnectionStatus.Unavailable)]
    public void NonReadyProvidersCannotAlert(ConnectionStatus status){Assert.Empty(Alert(Codex with{ConnectionStatus=status}));Assert.Empty(Alert(Claude with{ConnectionStatus=status}));}
    [Fact]public void UnknownStaleFutureAndEstimatedNeverBecomeLow()
    {
        foreach(var p in new[]{Codex,Claude})
        {
            Assert.Empty(Alert(p with{LastUpdated=null}));Assert.Empty(Alert(p with{LastUpdated=Now.AddMinutes(-8)}));
            Assert.Empty(Alert(p with{LastUpdated=Now.AddMinutes(1)}));Assert.Empty(Alert(p with{IsEstimated=true}));Assert.Empty(Alert(p with{NeedsSetup=true}));
        }
        Assert.Empty(Alert(Claude with{RemainingBalance=null}));Assert.Empty(Alert(Codex with{Windows=[]}));
        Assert.Empty(Alert(Dsh(state:DshBalanceState.Stale)));Assert.Empty(Alert(Dsh(state:DshBalanceState.Unavailable)));
        Assert.Empty(Alert(Dsh(at:Now.AddMinutes(-5))));Assert.Empty(Alert(Dsh(at:Now.AddMinutes(1))));Assert.Empty(Alert(Dsh() with{AccountBalance=DshBalanceReading.Empty}));
    }
    [Fact]public void DshUsagePartialOrUnavailableDoesNotInvalidateFreshOfficialWallet()
    {
        Assert.Single(Alert(Dsh()));Assert.Single(Alert(Dsh() with{ConnectionStatus=ConnectionStatus.Unavailable}));
        Assert.Empty(Alert(Dsh() with{ConnectionStatus=ConnectionStatus.Disabled}));
    }
    [Fact]public void ThresholdIsStrictAndZeroDisables()
    {
        Assert.Empty(Alert(Claude with{RemainingBalance=5}));Assert.Empty(Alert(Dsh("5")));
        Assert.Empty(Alert(Codex with{Windows=Codex.Windows.Select(w=>w with{UsedPercent=85}).ToArray()}));
        Assert.Empty(LowQuotaAlerts.Evaluate([Codex,Claude,Dsh()],new(){CodexFiveHourLowPercent=0,CodexWeeklyLowPercent=0,ClaudeLowBalanceUsd=0,DshLowBalance=0},Now));
        Assert.Empty(LowQuotaAlerts.Evaluate([Codex,Claude,Dsh()],new(){CodexEnabled=false,ClaudeEnabled=false,DshEnabled=false},Now));
    }
    [Fact]public void SelectedBucketMissingDurationsAndExpiredWindowsAreSafe()
    {
        var windows=Codex.Windows.Select(w=>w with{UsedPercent=0}).Concat([new QuotaWindow("other","Primary",99,300,Now.AddHours(1))]).ToArray();
        Assert.Empty(Alert(Codex with{Windows=windows}));Assert.Empty(Alert(Codex with{Windows=[new("codex","Primary",99,null,null)]}));
        Assert.Empty(Alert(Codex with{Windows=Codex.Windows.Select(w=>w with{ResetAt=Now}).ToArray()}));
        Assert.Empty(Alert(Codex with{Windows=[new("codex","Primary",double.NaN,300,null),new("codex","Secondary",101,10080,null)]}));
        Assert.Single(Alert(Codex with{Windows=[new("other","Primary",99,300,null)]}));
    }
    [Theory][InlineData("4.99999999999999999999999999999999",true)][InlineData("5.00000000000000000000000000000001",false)][InlineData("4.9e0",true)][InlineData("1e-1000",true)][InlineData("1e1000",false)][InlineData("NaN",false)][InlineData("-0.01",true)][InlineData("0",true)]
    public void OriginalDshAmountsComparedWithoutRounding(string value,bool low)=>Assert.Equal(low,Alert(Dsh(value)).Count==1);
    [Fact]public void CurrencyAndWalletAbsenceAreNotZeroOrFx()
    {
        var p=Dsh() with{AccountBalance=new(new([new("USD","1")],[],Now,"test"),DshBalanceState.Ready,null)};
        Assert.Empty(Alert(p));Assert.Single(Alert(p,new(){DshAlertCurrency="USD"}));
        Assert.Empty(Alert(p with{AccountBalance=new(new([],[],Now,"test"),DshBalanceState.Ready,null)}));
        Assert.Empty(Alert(Claude with{Currency="CNY"}));
        Assert.Empty(Alert(p with{AccountBalance=new(new([new("CNY","3"),new("CNY","2")],[],Now,"test"),DshBalanceState.Ready,null)}));
    }
    [Fact]public void OldSettingsMigrateDefaultsAndNewSettingsRoundtrip()
    {
        var old=JsonSerializer.Deserialize<MonitorSettings>("{\"TopCenter\":false,\"HeatmapScale\":\"relative\"}")!;
        Assert.Equal(15,old.CodexFiveHourLowPercent);Assert.Equal(5,old.DshLowBalance);Assert.Equal("relative",old.HeatmapScale);
        old.CodexFiveHourLowPercent=22;old.CodexWeeklyLowPercent=7;old.ClaudeLowBalanceUsd=2.5m;old.DshLowBalance=1.2m;old.DshAlertCurrency="USD";
        var read=JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(old))!;
        Assert.Equal(22,read.CodexFiveHourLowPercent);Assert.Equal(7,read.CodexWeeklyLowPercent);Assert.Equal(2.5m,read.ClaudeLowBalanceUsd);Assert.Equal(1.2m,read.DshLowBalance);Assert.Equal("USD",read.DshAlertCurrency);
    }
    [Theory][InlineData(1)][InlineData(1.25)][InlineData(1.5)][InlineData(2)][InlineData(3)]
    public void DpiAndNegativeMonitorDockingKeepCenterAndBounds(double scale)
    {
        var area=new DockBounds(-3840,-1200,0,960);
        var m=IslandDock.Place(area,scale,176,8,true,0,0);var c=IslandDock.Place(area,scale,520,82,true,0,8);var e=IslandDock.Place(area,scale,720,700,true,0,8);
        Assert.Equal(area.Top,m.Top);Assert.Equal((int)Math.Ceiling(8*scale),m.Bottom-m.Top);
        Assert.InRange(Math.Abs((m.Left+m.Right)-(c.Left+c.Right)),0,1);Assert.InRange(Math.Abs((m.Left+m.Right)-(e.Left+e.Right)),0,1);
        foreach(var r in new[]{m,c,e,IslandDock.Place(area,scale,720,1000,false,10000,8)}){Assert.True(r.Left>=area.Left&&r.Right<=area.Right&&r.Top>=area.Top&&r.Bottom<=area.Bottom);}
    }
    [Fact]public void CustomDockKeepsOriginalCapsuleAnchorAcrossStates()
    {
        var a=new DockBounds(0,0,2560,1528);var c=IslandDock.Place(a,1.5,520,82,false,400,8);var m=IslandDock.Place(a,1.5,176,8,false,400,0);Assert.Equal(400,c.Left);Assert.Equal(c.Left+c.Right,m.Left+m.Right);
    }
}
