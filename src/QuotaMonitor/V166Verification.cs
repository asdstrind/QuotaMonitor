using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

namespace QuotaMonitor;
public partial class App
{
    // Local synthetic acceptance only. No adapters, production settings, wallet or account reads.
    private async Task Verify166Async(bool preview,string? outputFolder=null)
    {
        var folder=outputFolder??Path.Combine(Environment.CurrentDirectory,"evidence","v1.6.6","gui");Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();var cases=new List<object>();var performance=new List<object>();
        IslandWindow? window=null;
        try{
            var today=DateOnly.FromDateTime(DateTime.Now);var now=DateTimeOffset.UtcNow;
            var config=new MonitorSettings{HideInFullscreen=false};var model=new MonitorViewModel();
            foreach(var id in UsageGrid.Order)model.Providers.Add(new(ProviderSnapshot.Empty(id,MonitorViewModel.NameOf(id),MetricType.Quota)));
            void Fixture(long? value=null,bool original=false,bool mixed=false)
            {
                foreach(var (id,mul) in new[]{("codex",21000L),("opencode-go",17000L),("dsh",32000L),("antigravity",47000L)})
                    model.UpdateHistory(new(){ProviderId=id,Days=Enumerable.Range(0,182).Select(i=>new UsageDay(today.AddDays(-i),original?((i*37)%71)*mul:mixed&&id=="dsh"?null:value,1,null,UsageQuality.Exact)).ToArray(),Source="synthetic fixture",Scope="UI regression only"},today);
            }
            void Mask(int mask){config.ShowCodex=(mask&1)!=0;config.ShowGo=(mask&2)!=0;config.ShowDsh=(mask&4)!=0;config.ShowAntigravity=(mask&8)!=0;model.ApplyVisibility(config,today);}
            model.Codex.Update(new(){ProviderId="codex",DisplayName="Codex",MetricType=MetricType.Quota,LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,Windows=[new("codex","Primary",90,300,now.AddHours(2)),new("codex","Secondary",92,10080,now.AddDays(2))],RemainingPercent=8});
            model.Go.Update(new(){ProviderId="opencode-go",DisplayName="OpenCode Go",MetricType=MetricType.Quota,LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,RemainingPercent=12,Windows=[new("rolling","5 小时",88,300,now.AddHours(2)),new("weekly","每周",91,10080,now.AddDays(2)),new("monthly","每月",94,null,now.AddDays(12))]});
            model.Dsh.Update(new(){ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY","3.28")],[new("CNY","10.50")],now,"synthetic fixture"),DshBalanceState.Ready,null)});
            model.Antigravity.Update(new(){ProviderId="antigravity",DisplayName="Antigravity",MetricType=MetricType.Quota,LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,Windows=[new("gemini-weekly","Gemini",85,10080,now.AddDays(4)),new("3p-weekly","Claude/GPT",92,10080,now.AddDays(5))]});
            Fixture(original:true);Mask(15);model.RefreshCommand=new ActionCommand(()=>{});model.SettingsCommand=new ActionCommand(()=>{var preferences=new SettingsWindow(config,new QuotaMonitor.Configuration.SettingsStore(Path.Combine(folder,"preview-settings")),new QuotaMonitor.Security.CredentialStore("QuotaMonitor/V170Preview"),new HttpClient(),()=>model.ApplyVisibility(config,today),null,false){Owner=window};preferences.Show();});
            window=new IslandWindow(model,config,()=>new QuotaMonitor.Configuration.SettingsStore(Path.Combine(folder,"preview-settings")).Save(config)){Title="QuotaMonitor v1.7.0 · Synthetic verification",ShowInTaskbar=preview,Background=new SolidColorBrush(Color.FromRgb(34,37,43))};MainWindow=window;
            window.VerificationSuspendsPointer=true;window.Show();window.SetState(IslandState.Expanded,false);await Task.Delay(250);
            var heatmap=(UsageHeatmap)window.FindName("Heatmap");var card=(Border)window.FindName("TipCard");var content=(MorphLayoutHost)window.FindName("ContentSurface");var surface=(MorphSurface)window.FindName("Island");var overlay=(Canvas)window.FindName("Overlay");
            Rect Bounds(FrameworkElement item)=>item.TransformToVisual(window).TransformBounds(new Rect(item.RenderSize));
            bool ValidCard(GridDay day)
            {
                if(!window.TipVisible)return false;
                var r=Bounds(card);var tile=heatmap.TransformToVisual(window).TransformBounds(heatmap.Tile(day));tile.Inflate(2,2);
                return surface.ContentClip.FillContainsWithDetail(new RectangleGeometry(r))==IntersectionDetail.FullyContains&&!r.IntersectsWith(tile)&&new Rect(window.RenderSize).Contains(r)&&!overlay.IsHitTestVisible&&FindVisual<TextBlock>(card).Where(t=>t.IsVisible&&t.Text.Length>0).All(t=>
                {
                    var text=new FormattedText(t.Text,System.Globalization.CultureInfo.CurrentUICulture,t.FlowDirection,new Typeface(t.FontFamily,t.FontStyle,t.FontWeight,t.FontStretch),t.FontSize,Brushes.White,VisualTreeHelper.GetDpi(t).PixelsPerDip);
                    return text.WidthIncludingTrailingWhitespace<=t.ActualWidth+.75;
                });
            }
            if(!preview){
                foreach(var mask in Enumerable.Range(0,16))
                {
                    Mask(mask);window.SetState(IslandState.Expanded,false);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                    if(mask==0){checks["EmptyNoHeatmap"]=!heatmap.IsVisible&&!window.TipVisible;Raster(window,Path.Combine(folder,"mask-00.png"),1);continue;}
                    foreach(var value in new long?[]{0,9_999_999,99_999_999,999_999_999,1_999_999_999,null,long.MaxValue})
                    {
                        Fixture(value);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                        var positions=new[]{(0,0),(0,6),(12,0),(12,3),(12,6),(24,6),(25,0),(25,6)};
                        var dates=positions.Select(p=>model.Grid!.DayAt(p.Item1,p.Item2)).OfType<GridDay>().Append(model.Grid!.Days[^1]).DistinctBy(d=>d.Date).ToArray();
                        foreach(var day in dates){
                            window.HoverDayForSmoke(day.Date);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            var ok=ValidCard(day);checks[$"Mask{mask}-Value{value?.ToString()??"unknown"}-{day.Column}-{day.Row}"]=ok;
                            cases.Add(new{Mask=mask,Rows=model.Tip!.Rows.Count,Value=value,day.Column,day.Row,day.IsToday,Valid=ok,Card=Bounds(card),Tile=heatmap.TransformToVisual(window).TransformBounds(heatmap.Tile(day)),Clip=surface.ContentClip.Bounds});
                            if(value==1_999_999_999&&(day.Column is 0 or 25)&&day.Row==0)foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"mask-{mask:D2}-col-{day.Column}-huge-{scale*100:0}.png"),scale);
                        }
                    }
                    Fixture(1_999_999_999,mixed:true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.HoverDayForSmoke(model.Grid!.Days[^1].Date);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);checks[$"Mask{mask}-MixedUnknown"]=ValidCard(model.Grid.Days[^1]);
                    window.HoverDayForSmoke(DateOnly.MinValue);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var layouts=content.Measures+content.Arranges;var renders=heatmap.RenderCount;var sw=Stopwatch.StartNew();
                    for(var i=0;i<300;i++){window.HoverDayForSmoke(model.Grid!.Days[(i*37)%model.Grid.Days.Count].Date);if(i%10==0)await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    checks[$"Mask{mask}-RapidNoMorphLayout"]=layouts==content.Measures+content.Arranges;
                    checks[$"Mask{mask}-RapidNoHeatmapRebuild"]=renders==heatmap.RenderCount;
                    performance.Add(new{Mask=mask,Hovers=300,ElapsedMs=sw.Elapsed.TotalMilliseconds,ExtraMorphLayouts=content.Measures+content.Arranges-layouts,ExtraHeatmapRenders=heatmap.RenderCount-renders});
                }
                Mask(13);Fixture(1_999_999_999);window.SetState(IslandState.Collapsed,false);await Task.Delay(60);
                window.SetState(IslandState.Expanded);var frames=new List<object>();var frameLayout=content.Measures+content.Arranges;
                EventHandler render=(_,_)=>{var day=heatmap.HoverDate(model.Grid!.Days[^1].Date)!;frames.Add(new{Visible=window.TipVisible,Valid=!window.TipVisible||ValidCard(day),window.VisualWidth,window.VisualHeight});};
                CompositionTarget.Rendering+=render;await Task.Delay(850);CompositionTarget.Rendering-=render;
                checks["AnimationNoClippedFrames"]=frames.Count>0&&frames.All(f=>(bool)f.GetType().GetProperty("Valid")!.GetValue(f)!);
                checks["AnimationEventuallyShows"]=window.TipVisible;
                checks["AnimationNoMorphLayouts"]=frameLayout==content.Measures+content.Arranges;
                foreach(var left in new[]{-10000d,10000d}){
                    config.TopCenter=false;config.Left=left;window.Reposition();await Task.Delay(160);window.UpdateLayout();
                    window.HoverDayForSmoke(model.Grid!.Days[^1].Date);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var day=model.Grid!.Days[^1];var bounds=Bounds(card);var tile=heatmap.TransformToVisual(window).TransformBounds(heatmap.Tile(day));
                    checks[$"ScreenBoundary{left}"]=ValidCard(day);
                    cases.Add(new{Boundary=left,Visible=window.TipVisible,Card=bounds,Tile=tile,Clip=surface.ContentClip.Bounds,ClipTest=surface.ContentClip.FillContainsWithDetail(new RectangleGeometry(bounds)).ToString(),Title=model.Tip?.Title,Window=window.WindowEvidence,Text=FindVisual<TextBlock>(card).Where(t=>t.IsVisible).Select(t=>new{t.Text,t.ActualWidth,t.ActualHeight}).ToArray()});
                    Raster(window,Path.Combine(folder,$"boundary-{left}.png"),1.5);
                }
                config.TopCenter=true;window.Reposition();
                await File.WriteAllTextAsync(Path.Combine(folder,"animation.json"),JsonSerializer.Serialize(frames));
            }
            Fixture(original:true);Mask(13);window.SetState(IslandState.Expanded,false);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.HoverDayForSmoke(model.Grid!.DayAt(25,0)!.Date);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"after-three-right-{scale*100:0}.png"),scale);
            await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(x=>x),Checks=checks,Cases=cases,Performance=performance,NativeDpi=VisualTreeHelper.GetDpi(window).DpiScaleX,RasterScales=new[]{100,150,200},SyntheticOnly=true},new JsonSerializerOptions{WriteIndented=true}));
            if(preview){
                NativeWindow.VerificationInspectable(new System.Windows.Interop.WindowInteropHelper(window).Handle);window.VerificationFixedInput=true;
                window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.F6)window.SetState(IslandState.Collapsed,false);if(e.Key==Key.F7)window.SetState(IslandState.Expanded);if(e.Key==Key.F10){Mask(13);window.SetState(IslandState.Expanded,false);}if(e.Key==Key.F12){Fixture(1_999_999_999);window.HoverDayForSmoke(model.Grid!.Days[^1].Date);}if(e.Key==Key.F11)window.HoverDayForSmoke(model.Grid!.DayAt(25,0)!.Date);};
                Mask(15);window.HoverDayForSmoke(DateOnly.MinValue);window.SetState(IslandState.Expanded,false);
                window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.F5){config.TopCenter=!config.TopCenter;window.Reposition();}if(e.Key==Key.F8)File.WriteAllText(Path.Combine(folder,"desktop-state.json"),JsonSerializer.Serialize(new{config.TopCenter,config.Left,config.Top,Window=window.WindowEvidence},new JsonSerializerOptions{IncludeFields=true}));};
                window.Closed+=(_,_)=>Shutdown();return;
            }
        }catch(Exception ex){checks["ExceptionFree"]=false;await File.WriteAllTextAsync(Path.Combine(folder,"failure.txt"),ex.ToString());}
        window?.Close();Shutdown(checks.Values.All(x=>x)?0:1);
    }
}
