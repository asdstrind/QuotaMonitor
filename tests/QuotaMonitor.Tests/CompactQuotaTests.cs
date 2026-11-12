using QuotaMonitor.Core.Models;
using QuotaMonitor.ViewModels;

namespace QuotaMonitor.Tests;

public sealed class CompactQuotaTests
{
    private static ProviderSnapshot Go(double five,double week,double month)=>new()
    {
        ProviderId="opencode-go",DisplayName="OpenCode Go",ConnectionStatus=ConnectionStatus.Ready,LastUpdated=DateTimeOffset.UtcNow,
        Windows=[new("rolling","5 小时",100-five,300,null),new("weekly","每周",100-week,10080,null),new("monthly","每月",100-month,null,null)]
    };
    [Theory]
    [InlineData(15,5,100,"weekly","5%")]
    [InlineData(15,10,2,"monthly","2%")]
    [InlineData(15,20,60,"rolling","15%")]
    [InlineData(50,30,20,"rolling","50%")]
    [InlineData(15,6,3,"rolling","15%")]
    [InlineData(80,0,90,"weekly","0%")]
    [InlineData(80,90,0,"monthly","0%")]
    public void GoShowsOneWindowUsingComparableRemainingAllowance(double five,double week,double month,string selected,string text)
    {
        var s=Go(five,week,month);Assert.Equal(selected,OpenCodeGoQuota.MostConstrained(s.Windows)?.Bucket);Assert.Equal(text,new ProviderViewModel(s).Compact);
    }
    [Theory][InlineData(ConnectionStatus.Unavailable)][InlineData(ConnectionStatus.Disabled)][InlineData(ConnectionStatus.Stale)]
    public void GoFailureAndStalenessNeverShowAnInventedZero(ConnectionStatus status)=>Assert.Equal("—",new ProviderViewModel(Go(15,5,100) with{ConnectionStatus=status}).Compact);
    [Fact] public void GoMissingAndInvalidWindowsStayUnknown()
    {
        var s=Go(15,5,100);Assert.Equal("—",new ProviderViewModel(s with{Windows=[]}).Compact);
        Assert.Equal("15%",new ProviderViewModel(s with{Windows=[s.Windows[0],s.Windows[1] with{UsedPercent=double.NaN},s.Windows[2] with{UsedPercent=-1}]}).Compact);
        Assert.Null(OpenCodeGoQuota.MostConstrained([new("unknown","unknown",100,null,null)]));
    }
    [Fact] public void ExpandedStillShowsAllThreeOriginalPercentages()
    {var vm=new ProviderViewModel(Go(15,5,100));Assert.Equal(new[]{"15","5","100"},vm.Gauges.Select(g=>g.Value));Assert.Equal(3,vm.Gauges.Count);}
    [Fact] public void CodexExhaustedWeeklyWindowOverridesPositivePrimary()
    {
        var now=DateTimeOffset.UtcNow;var s=new ProviderSnapshot{ProviderId="codex",DisplayName="Codex",ConnectionStatus=ConnectionStatus.Ready,LastUpdated=now,RemainingPercent=40,Windows=[new("codex","Primary",60,300,now.AddHours(1)),new("codex","Secondary",100,10080,now.AddDays(1))]};
        Assert.Equal("0%",new ProviderViewModel(s).Compact);
        Assert.Equal("40%",new ProviderViewModel(s with{Windows=s.Windows.Select(w=>w with{UsedPercent=w.Name=="Secondary"?99:60}).ToArray()}).Compact);
        Assert.Equal("40%",new ProviderViewModel(s with{Windows=[s.Windows[0],s.Windows[1] with{Bucket="other"}]}).Compact);
        Assert.Equal("40%",new ProviderViewModel(s with{Windows=[s.Windows[0],s.Windows[1] with{ResetAt=now.AddSeconds(-1)}]}).Compact);
    }
}
