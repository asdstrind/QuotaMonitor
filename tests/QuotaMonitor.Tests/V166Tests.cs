using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;
using ZstdSharp;

namespace QuotaMonitor.Tests;

public sealed class V166Tests
{
    private static readonly DateOnly Today=new(2026,11,3);
    [Fact] public void CalendarAndAllSavedHistoryHaveIndependentTotalsForEveryProvider()
    {
        var early=UsageGrid.WindowStart(Today,26).AddDays(-1);
        foreach(var id in UsageGrid.Order)
        {
            var h=new UsageHistory{ProviderId=id,Days=[new(early,1_600_000_001,1,null,UsageQuality.Exact),new(Today,60_000_009,2,null,UsageQuality.Partial),new(Today.AddDays(1),999,1,null,UsageQuality.Exact)]};
            var g=UsageGrid.Build([h],Today,visible:[id]);var p=Assert.Single(g.Providers);
            Assert.Equal(60_000_009,p.Tokens);Assert.Equal(1_660_000_010,p.Cumulative.Tokens);
            Assert.Equal(3,p.Cumulative.Requests);Assert.Equal(early,p.Cumulative.Start);Assert.Equal(Today,p.Cumulative.End);
            Assert.Equal(60_000_009,g.Days.Sum(d=>d.Total??0));Assert.DoesNotContain(g.Days,d=>d.Date==early);
            Assert.Equal(LaneState.Partial,g.Days[^1].Lanes[0].State);
            var vm=new MonitorViewModel();vm.UpdateHistory(h,Today);Assert.Equal(Format.Tokens(1_660_000_010),vm.Lanes.Single(l=>l.Id==id).Total);
        }
    }
    [Fact] public void UnknownHistoryDoesNotBecomeZeroAndDuplicatesDoNotIncreaseCumulative()
    {
        var day=new UsageDay(Today,null,null,null,UsageQuality.Partial);
        var vm=new MonitorViewModel();vm.UpdateHistory(new(){ProviderId="codex",Days=[day,day]},Today);
        Assert.Equal("—",vm.Lanes.Single(l=>l.Id=="codex").Total);Assert.Null(vm.Grid!.Providers[0].Cumulative.Tokens);
        day=day with{Tokens=0};vm.UpdateHistory(new(){ProviderId="codex",Days=[day,day]},Today);Assert.Equal("0",vm.Lanes.Single(l=>l.Id=="codex").Total);
        vm.UpdateHistory(new(){ProviderId="codex",Days=[day with{Tokens=long.MaxValue},day with{Date=Today.AddDays(-1),Tokens=1}]},Today);
        Assert.Null(vm.Grid!.Providers[0].Cumulative.Tokens);
    }
    [Fact] public void PlacementStaysInsideActualClipAtAllScalesCombinationsAndBoundaryCells()
    {
        foreach(var mask in Enumerable.Range(1,15))foreach(var dpi in new[]{1d,1.5,2d})
        foreach(var width in new[]{720d,880d})foreach(var x in new[]{125d,430d,740d})foreach(var y in new[]{250d,322d,394d})
        {
            var rows=UsageGrid.Order.Where((_,i)=>(mask&(1<<i))!=0).Count();
            var clip=new TipRect((880-width+24)/2,21,width-26,448);
            var safe=TooltipPlacement.SafeBounds(clip,28,dpi);
            var tile=new TipRect(Math.Clamp(x,clip.X+24,clip.Right-24),y,18,18);
            var p=TooltipPlacement.Place(safe,310,66+rows*22,tile,new(125,250,640,162),1/dpi);
            Assert.NotNull(p);Assert.True(safe.Contains(p.Value));Assert.Equal(0,p.Value.Overlap(tile));
            // Translate/scale all coordinates as when a window moves across a monitor boundary.
            var translated=safe with{X=safe.X-2000,Y=safe.Y+400};
            var moved=TooltipPlacement.Place(translated,310,66+rows*22,tile with{X=tile.X-2000,Y=tile.Y+400},new(-1875,650,640,162),1/dpi);
            Assert.NotNull(moved);Assert.Equal(p.Value.X-2000,moved.Value.X,6);Assert.Equal(p.Value.Y+400,moved.Value.Y,6);
        }
    }
    [Fact] public void AboveIsPreferredAndIncompleteMorphDefersUntilItCanFit()
    {
        var p=TooltipPlacement.Place(new(10,10,700,440),250,140,new(350,240,18,18),new(30,240,650,168),2);
        Assert.NotNull(p);Assert.Equal(98,p.Value.Y);
        var bottom=TooltipPlacement.Place(new(10,10,700,440),250,140,new(350,400,18,18),new(30,240,650,168),2);
        Assert.NotNull(bottom);Assert.Equal(0,bottom.Value.Overlap(new(30,240,650,168)));Assert.Equal(98,bottom.Value.Y);
        Assert.Null(TooltipPlacement.Place(new(0,0,250,80),300,150,new(10,10,18,18),new(0,0,250,80),1));
    }
    [Fact] public void OneToFourRowsPreserveHugeCountsUnknownAndToday()
    {
        foreach(var mask in Enumerable.Range(0,16))
        {
            var vm=new MonitorViewModel();var config=new MonitorSettings{ShowCodex=(mask&1)!=0,ShowGo=(mask&2)!=0,ShowDsh=(mask&4)!=0,ShowAntigravity=(mask&8)!=0};
            foreach(var id in UsageGrid.Order)vm.UpdateHistory(new(){ProviderId=id,Days=[new(Today,id=="dsh"?null:1_999_999_999,null,null,UsageQuality.Exact)]},Today);
            vm.ApplyVisibility(config,Today);var tip=vm.BuildTip(vm.Grid!.Days[^1]);
            Assert.Equal(UsageGrid.Order.Count(config.Visible),tip.Rows.Count);Assert.Contains("今天",tip.Title);
            Assert.All(tip.Rows,r=>Assert.Contains(r.Value,new[]{"—","1,999,999,999"}));
        }
    }

    private static object Record(string response,string at,long input,long cache,long output,long reasoning=0)=>new{timestamp=at,type="token_usage_record",payload=new{response_id=response,usage=new{input_tokens=input,cached_input_tokens=cache,output_tokens=output,reasoning_output_tokens=reasoning,total_tokens=input+output}}};
    private static object Counter(string at,long cumulative,long input,long cache,long output)=>new{timestamp=at,type="event_msg",payload=new{type="token_count",info=new{total_token_usage=new{total_tokens=cumulative},last_token_usage=new{input_tokens=input,cached_input_tokens=cache,output_tokens=output,total_tokens=input+output}}}};
    private static string Lines(params object[] rows)=>string.Join('\n',rows.Select(r=>JsonSerializer.Serialize(r)))+"\n";
    private static object Meta(string id)=>new{type="session_meta",payload=new{id}};
    [Fact] public void ModernMirrorsDoNotCountTwiceAndLaterLegacyResponsesAreNotLost()
    {
        var home=Path.Combine(Path.GetTempPath(),"quota166-"+Guid.NewGuid());var dir=Path.Combine(home,"sessions");Directory.CreateDirectory(dir);
        try{
            var text=Lines(Meta("same-session"),Counter("2026-11-01T10:00:00Z",110,100,80,10),Record("response2","2026-11-02T10:00:00Z",1_600_000_000,1_400_000_000,50_000_000,30_000_000),Counter("2026-11-02T10:00:25Z",1_650_000_110,1_600_000_000,1_400_000_000,50_000_000),Counter("2026-11-03T10:00:00Z",1_650_000_330,200,150,20));
            var path=Path.Combine(dir,"rollout-a.jsonl");File.WriteAllText(path,text);
            using(var c=new Compressor())File.WriteAllBytes(path+".zst",c.Wrap(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());
            var reader=new CodexUsageHistory(TimeZoneInfo.Utc);var h=reader.Read(home,Today,CancellationToken.None);
            Assert.Equal(1_650_000_330,h.Days.Sum(d=>d.Tokens??0));Assert.Equal(3,h.Days.Sum(d=>d.Requests??0));Assert.Equal(1_400_000_230,h.Days.Sum(d=>d.CachedTokens??0));
            File.AppendAllText(path,Lines(Counter("2026-11-03T11:00:00Z",1_650_000_660,300,200,30)));
            Assert.Equal(1_650_000_660,reader.Read(home,Today,CancellationToken.None).Days.Sum(d=>d.Tokens??0));
        }finally{Directory.Delete(home,true);}
    }
    [Fact] public void DifferentSessionsWithIdenticalCountersRemainDistinctAndCrossFormatCopiesMerge()
    {
        var home=Path.Combine(Path.GetTempPath(),"quota166-"+Guid.NewGuid());Directory.CreateDirectory(Path.Combine(home,"sessions"));Directory.CreateDirectory(Path.Combine(home,"archived_sessions"));
        try{
            var at="2026-11-03T10:00:00Z";
            File.WriteAllText(Path.Combine(home,"sessions","rollout-a.jsonl"),Lines(Meta("a"),Record("ra",at,100,80,10)));
            File.WriteAllText(Path.Combine(home,"archived_sessions","rollout-b.jsonl"),Lines(Meta("a"),Counter("2026-11-03T10:00:03Z",110,100,80,10)));
            File.WriteAllText(Path.Combine(home,"sessions","rollout-c.jsonl"),Lines(Meta("b"),Counter("2026-11-03T10:00:03Z",110,100,80,10)));
            var h=new CodexUsageHistory(TimeZoneInfo.Utc).Read(home,Today,CancellationToken.None);
            Assert.Equal(220,h.Days.Sum(d=>d.Tokens??0));Assert.Equal(2,h.Days.Sum(d=>d.Requests??0));
        }finally{Directory.Delete(home,true);}
    }
    [Fact] public void InvalidOnlyDayStaysUnknownAndCompressedFailureIsPartial()
    {
        var home=Path.Combine(Path.GetTempPath(),"quota166-"+Guid.NewGuid());var dir=Path.Combine(home,"sessions");Directory.CreateDirectory(dir);
        try{
            File.WriteAllText(Path.Combine(dir,"rollout-2026-11-02T10-00-00-a.jsonl"),Lines(new{timestamp="2026-11-02T10:00:00Z",type="token_usage_record",payload=new{usage=new{input_tokens=-1}}}));
            File.WriteAllBytes(Path.Combine(dir,"rollout-2026-11-03T10-00-00-b.jsonl.zst"),[1,2,3]);
            var h=new CodexUsageHistory(TimeZoneInfo.Utc).Read(home,Today,CancellationToken.None);
            Assert.All(h.Days,d=>{Assert.Null(d.Tokens);Assert.Equal(UsageQuality.Partial,d.Quality);});
        }finally{Directory.Delete(home,true);}
    }
    [Fact] public void DelayedMirrorsAndResumedCounterSegmentsDoNotInventDeltas()
    {
        var home=Path.Combine(Path.GetTempPath(),"quota166-"+Guid.NewGuid());var dir=Path.Combine(home,"sessions");Directory.CreateDirectory(dir);
        try{
            File.WriteAllText(Path.Combine(dir,"rollout-a.jsonl"),Lines(Meta("same"),
                Record("r1","2026-11-03T10:00:00Z",100,80,10),Record("r2","2026-11-03T10:00:01Z",200,180,20),
                Counter("2026-11-03T10:00:02Z",110,100,80,10),Record("r3","2026-11-03T10:00:03Z",300,280,30),
                Counter("2026-11-03T10:00:04Z",330,200,180,20),Counter("2026-11-03T10:00:05Z",660,300,280,30)));
            // Another continuation shares the session but starts with a different cumulative base.
            File.WriteAllText(Path.Combine(dir,"rollout-b.jsonl"),Lines(Meta("same"),Counter("2026-11-03T10:00:03Z",1044,40,20,4),Counter("2026-11-03T10:00:06Z",1099,50,30,5)));
            var h=new CodexUsageHistory(TimeZoneInfo.Utc).Read(home,Today,CancellationToken.None);
            Assert.Equal(759,h.Days.Sum(d=>d.Tokens??0));Assert.Equal(5,h.Days.Sum(d=>d.Requests??0));
        }finally{Directory.Delete(home,true);}
    }
    [Fact] public void RawHistoryBeforeCalendarWindowAndLocalMidnightAreRetained()
    {
        var home=Path.Combine(Path.GetTempPath(),"quota166-"+Guid.NewGuid());Directory.CreateDirectory(Path.Combine(home,"archived_sessions"));
        try{
            File.WriteAllText(Path.Combine(home,"archived_sessions","rollout-early.jsonl"),Lines(Meta("early"),Record("early","2026-03-01T23:59:00Z",1_500_000_000,1_000_000_000,10),Record("late","2026-11-02T16:01:00Z",100,50,20)));
            var zone=TimeZoneInfo.CreateCustomTimeZone("test+8",TimeSpan.FromHours(8),"+8","+8");
            var h=new CodexUsageHistory(zone).Read(home,Today,CancellationToken.None);var grid=UsageGrid.Build([h],Today,visible:["codex"]);
            Assert.Equal(new DateOnly(2026,3,2),h.CoverageStart);Assert.Equal(120,grid.Providers[0].Tokens);Assert.Equal(1_500_000_130,grid.Providers[0].Cumulative.Tokens);
            Assert.Equal(120,h.Days.Single(d=>d.Date==Today).Tokens);
        }finally{Directory.Delete(home,true);}
    }
}
