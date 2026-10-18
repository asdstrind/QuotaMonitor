using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Security;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

namespace QuotaMonitor;
public partial class App
{
    // Isolated synthetic data only: never starts adapters, receiver, production store or credentials.
    private async Task VerifyDesign164Async(bool preview)
    {
        var args=Environment.GetCommandLineArgs();
        var v163=true; var baseline=args.Contains("--baseline");
        var v162=v163||args.Contains("--verify-v162")||args.Contains("--preview-v162");
        var name=args.SkipWhile(a=>a!="--evidence-name").Skip(1).FirstOrDefault()??"after";
        if(name.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'))name="after";
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","ui-v1.6.4",name);Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();var timings=new List<object>();
        IslandWindow? window=null;SettingsWindow? prefs=null;
        try
        {
            var today=DateOnly.FromDateTime(DateTime.Now);var now=DateTimeOffset.UtcNow;
            var config=new MonitorSettings{HideInFullscreen=false,AntigravityGeminiLowPercent=20};var model=new MonitorViewModel();
            foreach(var id in UsageGrid.Order)model.Providers.Add(new(ProviderSnapshot.Empty(id,MonitorViewModel.NameOf(id),MetricType.Quota)));
            void Fixture(int mode=0)
            {
                model.Codex.Update(new(){ProviderId="codex",DisplayName="Codex",MetricType=MetricType.Quota,LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,Windows=[new("codex","Primary",90,300,now.AddHours(2)),new("codex","Secondary",92,10080,now.AddDays(2))],RemainingPercent=8});
                model.Claude.Update(new(){ProviderId="claude",DisplayName="ClaudeCN",LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,RemainingBalance=2.35m,Currency="USD"});
                model.Dsh.Update(new(){ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY","3.28")],[new("CNY","10.50")],now,"synthetic fixture"),DshBalanceState.Ready,null)});
                model.Antigravity.Update(new(){ProviderId="antigravity",DisplayName="Antigravity",MetricType=MetricType.Quota,LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,Windows=[new("gemini-weekly","Gemini",mode is 1 or 3?85:0,10080,now.AddDays(4)),new("3p-weekly","Claude/GPT",mode is 2 or 3?92:0,10080,now.AddDays(5))]});model.UpdateAlerts(config,now);
            }
            foreach(var (id,multiplier) in (v162?new[]{("codex",21000L),("claude",17000L),("dsh",32000L),("antigravity",47000L)}:new[]{("codex",21000L),("claude",17000L),("dsh",32000L)}))
                model.UpdateHistory(new(){ProviderId=id,Days=Enumerable.Range(0,182).Select(i=>new UsageDay(today.AddDays(-i),((i*37)%71)*multiplier,1,null,i%17==0?UsageQuality.Partial:UsageQuality.Exact,now)).ToArray(),Source="synthetic fixture",Scope="UI regression only"},today);
            model.ApplyVisibility(config,today);Fixture();
            model.RefreshCommand=new ActionCommand(()=>Fixture());
            model.SettingsCommand=new ActionCommand(()=>{prefs=new SettingsWindow(config,new SettingsStore(Path.Combine(folder,"settings")),new CredentialStore("QuotaMonitor/V161Synthetic/"+Environment.ProcessId),new HttpClient(),()=>model.ApplyVisibility(config,today),null,false){Owner=window};prefs.Show();});
            window=new IslandWindow(model,config,()=>{}){Title=(baseline?"QuotaMonitor v1.6.3":"QuotaMonitor v1.6.4")+" · Synthetic verification",ShowInTaskbar=preview,ShowActivated=preview};MainWindow=window;window.VerificationSuspendsPointer=true;window.Show();
            if(preview){NativeWindow.VerificationInspectable(new System.Windows.Interop.WindowInteropHelper(window).Handle);window.VerificationFixedInput=true;}
            await Task.Delay(400);
            var heatmap=(UsageHeatmap)window.FindName("Heatmap");
            // Same harness and fixture on both binaries. Render callbacks are measured, not GPU presentation.
            if(!args.Contains("--interactive-only")&&!args.Contains("--skip-benchmark"))foreach(var direction in new[]{"open","close-collapsed","close-minimal","reverse"})
            for(var iteration=0;iteration<13;iteration++)
            {
                window.SetState(direction=="open"?IslandState.Collapsed:IslandState.Expanded,false);await Task.Delay(150);
                var intervals=new List<double>();var content=new List<object>();var last=0d;var first=0d;var watch=Stopwatch.StartNew();
                var layoutEvents=0;EventHandler layout=(_,_)=>layoutEvents++;
                EventHandler rendering=(_,e)=>{
                    var ms=watch.Elapsed.TotalMilliseconds;if(last>0)intervals.Add(ms-last);else first=ms;last=ms;
                    var details=(FrameworkElement)window.FindName("Details");var compact=(FrameworkElement)window.FindName("Compact");
                    content.Add(new{Ms=ms,DetailsVisible=details.IsVisible,DetailsOpacity=details.Opacity,CompactVisible=compact.IsVisible,CompactOpacity=compact.Opacity,Width=window.VisualWidth,Height=window.VisualHeight,State=window.State.ToString()});
                };
                window.LayoutUpdated+=layout;CompositionTarget.Rendering+=rendering;
                window.MotionSamples.Clear();window.RecordMotion=true;
                var renders=heatmap.RenderCount;var renderCpu=heatmap.RenderCpuMs;
                var start=Stopwatch.GetTimestamp();window.SetState(direction=="open"?IslandState.Expanded:direction=="close-collapsed"?IslandState.Collapsed:IslandState.Minimal);var sync=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if(direction=="reverse"){await Task.Delay(60);window.SetState(IslandState.Expanded);await Task.Delay(60);window.SetState(IslandState.Minimal);await Task.Delay(60);window.SetState(IslandState.Expanded);}
                await Task.Delay(direction=="open"||direction=="reverse"?820:400);
                window.RecordMotion=false;CompositionTarget.Rendering-=rendering;window.LayoutUpdated-=layout;
                timings.Add(new{Direction=direction,Iteration=iteration,SyncMs=sync,FirstFrameMs=first,Intervals=intervals,LayoutEvents=layoutEvents,HeatmapRenders=heatmap.RenderCount-renders,HeatmapCpuMs=heatmap.RenderCpuMs-renderCpu,Motion=window.MotionSamples.ToArray(),Content=content});
            }
            await File.WriteAllTextAsync(Path.Combine(folder,"performance.json"),JsonSerializer.Serialize(new{Baseline=baseline,NativeDpi=VisualTreeHelper.GetDpi(window).DpiScaleX,RenderTier=RenderCapability.Tier>>16,ShellDurationMs=280,CloseDurationMs=320,HeatmapRevealMs=620,HeatmapDelayMs=90,Trials=timings},new JsonSerializerOptions{WriteIndented=true}));
            if(!args.Contains("--benchmark-only")&&!args.Contains("--interactive-only"))
            {
                var history=JsonSerializer.Serialize(model.Histories);
                for(var mask=0;mask<16;mask++)
                {
                    config.ShowCodex=(mask&1)!=0;config.ShowClaude=(mask&2)!=0;config.ShowDsh=(mask&4)!=0;config.ShowAntigravity=(mask&8)!=0;model.ApplyVisibility(config,today);
                    window.SetState(IslandState.Collapsed,false);await Task.Delay(60);window.UpdateLayout();
                    var compact=(FrameworkElement)window.FindName("Compact");
                    var items=FindVisual<ItemsControl>(compact).First();
                    var panels=FindVisual<StackPanel>(compact).Where(p=>p.Orientation==Orientation.Horizontal&&p.DataContext is ProviderViewModel).ToArray();
                    var boxes=panels.Select(p=>p.TransformToVisual(compact).TransformBounds(new Rect(p.RenderSize))).ToArray();
                    checks[$"Mask{mask:D2}Count"]=items.Items.Count==UsageGrid.Order.Count(config.Visible);
                    checks[$"Mask{mask:D2}NoOverlap"]=boxes.Zip(boxes.Skip(1)).All(p=>p.First.Right<=p.Second.Left+.1)&&boxes.All(b=>b.Left>=0&&b.Right<=compact.ActualWidth+.1);
                    checks[$"Mask{mask:D2}NoTextTrim"]=FindVisual<TextBlock>(compact).Where(t=>t.IsVisible).All(t=>{t.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return t.DesiredSize.Width-t.Margin.Left-t.Margin.Right<=t.ActualWidth+.6;});
                    if(v163)
                    {
                        checks[$"Mask{mask:D2}CompactTypography"]=FindVisual<TextBlock>(compact).Where(t=>t.DataContext is ProviderViewModel p&&t.Text==p.Name).All(t=>t.FontFamily.Source=="Bahnschrift, Segoe UI"&&t.FontWeight==FontWeights.SemiBold&&t.FontSize==12);
                        checks[$"Mask{mask:D2}NoCompactTooltips"]=FindVisual<FrameworkElement>(compact).All(e=>e.ToolTip is null);
                    }
                    if(v162)
                    {
                        var marks=FindVisual<HarnessMark>(compact).Where(m=>m.IsVisible).ToArray();
                        checks[$"Mask{mask:D2}AllCompactIcons"]=marks.Length==items.Items.Count&&marks.All(m=>m.Provider==((ProviderViewModel)m.DataContext).Id&&m.ActualWidth==16&&m.ActualHeight==16);
                        checks[$"Mask{mask:D2}NameColors"]=FindVisual<TextBlock>(compact).Where(t=>t.DataContext is ProviderViewModel p&&t.Text==p.Name).All(t=>(baseline?t.Foreground.ToString().EndsWith(((ProviderViewModel)t.DataContext).Accent[1..],StringComparison.OrdinalIgnoreCase):t.Foreground.ToString()=="#FFA4A9B3"));
                    }
                    if(boxes.Length>0)checks[$"Mask{mask:D2}Centered"]=Math.Abs(boxes.Min(b=>b.Left)+boxes.Max(b=>b.Right)-compact.ActualWidth)/2<=.51/VisualTreeHelper.GetDpi(window).DpiScaleX;
                    foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"mask-{mask:D2}-collapsed-{scale*100:0}.png"),scale);
                    window.SetState(IslandState.Expanded,false);await Task.Delay(60);
                    checks[$"Mask{mask:D2}Expanded"]=((ItemsControl)window.FindName("Instruments")).Items.Count==items.Items.Count;
                    foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"mask-{mask:D2}-expanded-{scale*100:0}.png"),scale);
                    if(v162)
                    {
                        var strip=(FrameworkElement)window.FindName("Instruments");
                        checks[$"Mask{mask:D2}AllExpandedIcons"]=FindVisual<HarnessMark>(strip).Where(m=>m.IsVisible).Count()==items.Items.Count;
                        checks[$"Mask{mask:D2}ExpandedNameColors"]=FindVisual<TextBlock>(strip).Where(t=>t.DataContext is ProviderViewModel p&&t.Text==p.Name).All(t=>t.Foreground.ToString()=="#FFF2F3F5");
                    }
                    checks[$"Mask{mask:D2}History"]=history==JsonSerializer.Serialize(model.Histories);
                }
                config.ShowCodex=config.ShowClaude=config.ShowDsh=config.ShowAntigravity=true;model.ApplyVisibility(config,today);window.SetState(IslandState.Expanded,false);await Task.Delay(100);
                if(!baseline)
                {
                    var strip=(FrameworkElement)window.FindName("Instruments");
                    var firstReadouts=FindVisual<TextBlock>(strip).Where(t=>t.FontSize is 26 or 30).GroupBy(t=>t.DataContext is ProviderViewModel p?p.Id:FindProvider(t)?.Id??"").Select(g=>g.OrderBy(t=>t.TransformToVisual(window).Transform(new Point()).Y).First()).ToArray();
                    var baselines=firstReadouts.Select(t=>new{Provider=t.DataContext is ProviderViewModel p?p.Id:FindProvider(t)?.Id,Y=t.TransformToVisual(window).Transform(new Point(0,t.BaselineOffset)).Y,Font=t.FontSize}).ToArray();
                    await File.WriteAllTextAsync(Path.Combine(folder,"first-baselines.json"),JsonSerializer.Serialize(baselines,new JsonSerializerOptions{WriteIndented=true}));
                    checks["FirstNumericBaselines"]=baselines.Length==4&&baselines.Max(b=>b.Y)-baselines.Min(b=>b.Y)<.7;
                    checks["NormalHeadersHaveNoStatus"]=FindVisual<TextBlock>(strip).Where(t=>System.Windows.Data.BindingOperations.GetBinding(t,TextBlock.TextProperty)?.Path.Path=="StateText").All(t=>!t.IsVisible);
                    foreach(var id in UsageGrid.Order)
                    {
                        var p=model.Providers.Single(p=>p.Id==id);var original=p.Snapshot;
                        p.Update(original with{ConnectionStatus=ConnectionStatus.Stale,AccountBalance=id=="dsh"?original.AccountBalance! with{State=DshBalanceState.Stale}:original.AccountBalance});window.UpdateLayout();
                        checks["WarningStatus"+id]=FindVisual<TextBlock>(strip).Any(t=>t.DataContext==p&&t.IsVisible&&t.Text=="已过期");
                        p.Update(original);
                    }
                    var heading=(FrameworkElement)window.FindName("HeatmapHeading");var legend=(FrameworkElement)window.FindName("HarnessLegend");
                    var firstLegend=FindVisual<HarnessMark>(legend).First();
                    checks["HeadingLegendAlignment"]=Math.Abs(heading.TransformToVisual(window).Transform(new Point()).X-firstLegend.TransformToVisual(window).Transform(new Point()).X)<1/VisualTreeHelper.GetDpi(window).DpiScaleX+.01;
                    var todayPen=(Pen)typeof(UsageHeatmap).GetField("TodayPen",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
                    var hoverPen=(Pen)typeof(UsageHeatmap).GetField("HoverPen",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
                    checks["DistinctTodayAndHover"]=todayPen.Thickness==1&&hoverPen.Thickness==1.5&&todayPen.Brush.ToString()=="#5CFFFFFF"&&hoverPen.Brush.ToString()=="#FFFFFFFF";
                    foreach(var (id,level) in UsageGrid.Order.SelectMany(id=>new[]{.25,.55,.9}.Select(level=>(id,level))))
                    {
                        model.HoverLane=id;var rgb=HeatmapColor.At(id,level);var expected=$"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
                        checks[$"Intensity{id}{level}"]=(level==.25?model.IntensityLow:level==.55?model.IntensityMid:model.IntensityHigh)==expected;
                    }
                    model.HoverLane=null;checks["IntensityRestoresNeutral"]=model.IntensityLow=="#20252A"&&model.IntensityMid=="#4F565F"&&model.IntensityHigh=="#9EA7B3";
                }
                var before=window.MorphEvidence.ToString();var mapSize=heatmap.RenderSize;
                foreach(var id in UsageGrid.Order)
                {
                    model.HoverLane=id;await Task.Delay(200);
                    checks["Hover"+id]=heatmap.FocusLane==id&&model.FocusLane==id;
                    checks["HoverStable"+id]=mapSize==heatmap.RenderSize&&before==window.MorphEvidence.ToString();
                    foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"hover-{id}-{scale*100:0}.png"),scale);
                    model.HoverLane=null;await Task.Delay(180);checks["Leave"+id]=heatmap.FocusLane is null;
                }
                window.HoverDayForSmoke(today);checks["DayTooltipPreserved"]=window.TipVisible&&model.Tip?.Rows.Count==4;
                await Task.Delay(120);window.UpdateLayout();Raster(window,Path.Combine(folder,"day-tooltip.png"),1.5);window.HoverDayForSmoke(DateOnly.MinValue);
                var legends=FindVisual<Border>(window).Where(b=>b.Tag is string id&&UsageGrid.Order.Contains(id)).ToArray();
                if(v162)
                {
                    checks["AntigravityHistoryInGrid"]=model.Grid!.Providers.Single(p=>p.Id=="antigravity").Tokens>0;
                    checks["AntigravityCanDominate"]=model.Grid.Days.Any(d=>d.Dominant?.ProviderId=="antigravity");
                    var original=model.Histories.Single(h=>h.ProviderId=="antigravity");
                    model.UpdateHistory(original with{Days=original.Days.Select(d=>d with{Quality=UsageQuality.Partial}).ToArray(),Error="Partial fixture"},today);
                    await Task.Delay(100);window.UpdateLayout();
                    checks["AntigravityMatchesTokenFormat"]=model.Lanes.Single(l=>l.Id=="antigravity").Total==Format.Tokens(model.Grid!.Providers.Single(p=>p.Id=="antigravity").Tokens)&&model.Lanes.Single(l=>l.Id=="antigravity").HasIssue;
                    checks["AntigravityTokenNumberRendered"]=FindVisual<TextBlock>(window).Any(t=>t.IsVisible&&t.Text==model.Lanes.Single(l=>l.Id=="antigravity").Total);
                    checks["LegendWithinPanel"]=FindVisual<Border>(window).Where(b=>b.Tag is string id&&UsageGrid.Order.Contains(id)).All(b=>b.TransformToVisual(window).TransformBounds(new Rect(b.RenderSize)).Right<=window.ActualWidth);
                    foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"partial-expanded-{scale*100:0}.png"),scale);
                    model.UpdateHistory(original,today);
                }
                checks["NoLegendTooltips"]=legends.Length==4&&legends.All(b=>b.ToolTip is null);
                if(v163)
                {
                    checks["NoApplicationTooltips"]=FindVisual<FrameworkElement>(window).All(e=>e.ToolTip is null);
                    checks["LegendIconsOnlyWithNamesAndNumbers"]=legends.All(b=>FindVisual<HarnessMark>(b).Count()==1&&!FindVisual<TextBlock>(b).Any(t=>System.Windows.Data.BindingOperations.GetBinding(t,TextBlock.TextProperty)?.Path.Path=="Status"));
                    foreach(var legend in legends)
                    {
                        var id=(string)legend.Tag;
                        legend.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
                        await Task.Delay(180);checks["LegendEnterEvent"+id]=model.FocusLane==id&&heatmap.FocusLane==id;
                        legend.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseLeaveEvent});
                        await Task.Delay(180);checks["LegendLeaveEvent"+id]=model.FocusLane is null&&heatmap.FocusLane is null;
                    }
                    model.HoverLane="antigravity";var dayTip=model.BuildTip(model.Grid!.Days[^1]);
                    checks["DayTipAllSelectedEvenDuringFocus"]=dayTip.Rows.Count==4&&dayTip.Rows.All(r=>r.Flag is null&&r.Detail=="")&&dayTip.Note!.StartsWith("合计 ");model.HoverLane=null;
                }
                checks["DshExpandedTime"]=model.Dsh.StateText.Length>0&&model.Dsh.Updated.Contains("同步");
                checks["DshCompactNoTime"]=!FindVisual<TextBlock>((FrameworkElement)window.FindName("Compact")).Any(t=>System.Windows.Data.BindingOperations.GetBinding(t,TextBlock.TextProperty)?.Path.Path=="CompactUpdated");
                var compactReadout=(FrameworkElement)window.FindName("Compact");
                var beforeRefreshWidth=window.VisualWidth;
                window.SetState(IslandState.Collapsed,false);model.Antigravity.Update(model.Antigravity.Snapshot with{ConnectionStatus=ConnectionStatus.Stale});await Task.Delay(360);
                model.Antigravity.Update(model.Antigravity.Snapshot with{ConnectionStatus=ConnectionStatus.Ready});await Task.Delay(360);
                checks["CompactResizesOnRefresh"]=FindVisual<TextBlock>(compactReadout).Where(t=>t.DataContext==model.Antigravity).All(t=>{t.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return t.DesiredSize.Width-t.Margin.Left-t.Margin.Right<=t.ActualWidth+.6;});
                window.SetState(IslandState.Expanded,false);
                foreach(var scale in new[]{1d,1.5,2})
                {
                    // Raster-space roundtrips include tile edges and gaps at each requested scale.
                    checks[$"TileHit{scale*100:0}"]=model.Grid!.Days.All(day=>{
                        var tile=heatmap.Tile(day);var pixel=new Point((tile.Left+tile.Width/2)*scale,(tile.Top+tile.Height/2)*scale);
                        return heatmap.DayAtPoint(new Point(pixel.X/scale,pixel.Y/scale))?.Date==day.Date&&heatmap.DayAtPoint(new Point(tile.Right+.5,tile.Top+tile.Height/2)) is null;
                    });
                }
                for(var mode=1;mode<=3;mode++)
                {
                    Fixture(mode);window.SetState(IslandState.Minimal,false);await Task.Delay(80);
                    checks["AgOneIcon"+mode]=model.MinimalAlerts.Count(a=>a.Key=="antigravity")==1;
                    var alert=(AlertReadout)window.FindName("AlertStrip");var placements=alert.WeeklyPlacements;
                    checks["WeeklyLabels"+mode]=placements.Select(p=>p.Label).SequenceEqual(mode==1?(baseline?new[]{"Ge"}:new[]{"Gemini"}):mode==2?(baseline?new[]{"A/O"}:new[]{"Claude/GPT"}):(baseline?new[]{"Ge","A/O"}:new[]{"Gemini","Claude/GPT"}));
                    checks["WeeklyHorizontal"+mode]=placements.All(p=>p.LabelBounds.Right+5<=p.ValueBounds.Left&&Math.Abs(p.LabelBaseline-p.ValueBaseline)<.01&&p.LabelBounds.Top>=0&&p.ValueBounds.Bottom<=26);
                    await File.WriteAllTextAsync(Path.Combine(folder,$"alert-{mode}-layout.json"),JsonSerializer.Serialize(placements,new JsonSerializerOptions{WriteIndented=true}));
                    foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"alert-{mode}-{scale*100:0}.png"),scale);
                }
                var alertSnapshot=config.ClaudeLowBalanceUsd;config.CodexFiveHourLowPercent=config.CodexWeeklyLowPercent=config.AntigravityGeminiLowPercent=config.AntigravityClaudeGptLowPercent=0;config.ClaudeLowBalanceUsd=config.DshLowBalance=0;model.UpdateAlerts(config,now);window.SetState(IslandState.Minimal,false);
                foreach(var scale in new[]{1d,1.5,2})Raster(window,Path.Combine(folder,$"minimal-{scale*100:0}.png"),scale);
                config.ClaudeLowBalanceUsd=alertSnapshot;
                model.SettingsCommand.Execute(null);await Task.Delay(100);
                if(!baseline)
                {
                    checks["SettingsVersionSource"]=((TextBlock)prefs!.FindName("VersionLabel")).Text==AppVersion.Display&&AppVersion.Display=="v1.6.4";
                    var save=(Button)prefs!.FindName("SaveButton");checks["SettingsSavePalette"]=save.Background.ToString()=="#FF203A32"&&save.Foreground.ToString()=="#FFA6F1D9"&&save.BorderBrush.ToString()=="#FF86DEC3";
                    var close=(Button)prefs.FindName("CloseButton");checks["GhostCloseSize"]=close.Width==28&&close.Height==28;
                    checks["GeneralExplanationRemoved"]=!FindVisual<TextBlock>((FrameworkElement)prefs.FindName("GeneralPage")).Any(t=>t.Text.Contains("自动刷新")||t.Text.Contains("Minimal →"));
                    ((RadioButton)prefs.FindName("ProvidersNav")).IsChecked=true;((RadioButton)prefs.FindName("ClaudeTab")).IsChecked=true;await Task.Delay(180);
                    foreach(var inputName in new[]{"TokenBox","UserIdBox"})
                    {
                        var input=(Control)prefs.FindName(inputName);input.Focus();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                        checks["Focus"+inputName]=input.IsKeyboardFocusWithin&&FindVisual<Border>(input).Any(b=>b.Name=="Input"&&b.BorderBrush.ToString()=="#FF86DEC3");
                        Raster(prefs,Path.Combine(folder,"settings-focus-"+inputName+".png"),VisualTreeHelper.GetDpi(prefs).DpiScaleX);
                    }
                    ((RadioButton)prefs.FindName("AlertsNav")).IsChecked=true;await Task.Delay(180);
                    var combo=(ComboBox)prefs.FindName("DshAlertCurrencyBox");combo.Focus();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    checks["FocusComboBox"]=combo.IsKeyboardFocusWithin&&FindVisual<Border>(combo).Any(b=>b.Name=="FocusRing"&&b.BorderBrush.ToString()=="#FF86DEC3");
                    foreach(var buttonName in new[]{"SaveButton","CloseButton"})
                    {
                        var button=(Button)prefs.FindName(buttonName);button.Focus();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                        checks["Focus"+buttonName]=button.IsKeyboardFocused&&FindVisual<Border>(button).Any(b=>b.BorderBrush.ToString()=="#FF86DEC3");
                    }
                    ((RadioButton)prefs.FindName("GeneralNav")).IsChecked=true;await Task.Delay(180);
                }
                if(v162)
                {
                    var providerNames=new[]{"Codex","Claude","Dsh","Antigravity"};var providerIds=new[]{"codex","claude","dsh","antigravity"};
                    for(var n=0;n<4;n++)
                    {
                        var check=(CheckBox)prefs!.FindName("Show"+providerNames[n]+"Check");
                        checks["SettingsCheckIcon"+n]=FindVisual<HarnessMark>(check).Single().Provider==providerIds[n];
                        checks["SettingsCheckColor"+n]=FindVisual<TextBlock>(check).Where(t=>t.Text==MonitorViewModel.NameOf(providerIds[n])).All(t=>t.Foreground.ToString()=="#FFF2F3F5");
                    }
                    foreach(var scale in new[]{1d,1.5,2})Raster(prefs!,Path.Combine(folder,$"settings-general-{scale*100:0}.png"),scale);
                    ((RadioButton)prefs!.FindName("ProvidersNav")).IsChecked=true;await Task.Delay(60);
                    for(var n=0;n<4;n++)
                    {
                        ((RadioButton)prefs.FindName(providerNames[n]+"Tab")).IsChecked=true;prefs.UpdateLayout();
                        var tab=(RadioButton)prefs.FindName(providerNames[n]+"Tab");var panel=(FrameworkElement)prefs.FindName(providerNames[n]+"Panel");
                        checks["SettingsTabIcon"+n]=FindVisual<HarnessMark>(tab).Single().Provider==providerIds[n];
                        checks["SettingsPanelIcon"+n]=FindVisual<HarnessMark>(panel).Single().Provider==providerIds[n];
                        checks["SettingsPanelColor"+n]=FindVisual<TextBlock>(panel).First(t=>t.FontSize==19).Foreground.ToString()==(baseline?new[]{"#FF86DEC3","#FFE9AB8D","#FF89B8FA","#FFB59AF7"}[n]:"#FFF2F3F5");
                        checks["SettingsTabNoTrim"+n]=FindVisual<TextBlock>(tab).All(t=>{t.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return t.DesiredSize.Width<=t.ActualWidth+.6;});
                        foreach(var scale in new[]{1d,1.5,2})Raster(prefs,Path.Combine(folder,$"settings-provider-{n}-{scale*100:0}.png"),scale);
                    }
                }
                for(var mask=0;mask<16;mask++)
                {
                    var names=new[]{"Codex","Claude","Dsh","Antigravity"};
                    for(var n=0;n<4;n++)((CheckBox)prefs!.FindName("Show"+names[n]+"Check")).IsChecked=(mask&(1<<n))!=0;
                    prefs!.UpdateLayout();checks["SettingsMask"+mask]=names.Select((id,n)=>((RadioButton)prefs.FindName(id+"Tab")).Visibility==((mask&(1<<n))!=0?Visibility.Visible:Visibility.Collapsed)).All(v=>v);
                }
                foreach(var scale in new[]{1d,1.5,2})Raster(prefs!,Path.Combine(folder,$"settings-{scale*100:0}.png"),scale);
                foreach(var id in new[]{"Codex","Claude","Dsh","Antigravity"})((CheckBox)prefs!.FindName("Show"+id+"Check")).IsChecked=true;
                foreach(var nav in new[]{"General","Alerts","Advanced"})
                {
                    ((RadioButton)prefs!.FindName(nav+"Nav")).IsChecked=true;await Task.Delay(180);
                    if(nav=="Advanced")foreach(var exp in FindVisual<Expander>(prefs))exp.IsExpanded=true;
                    foreach(var scale in new[]{1d,1.5,2})Raster(prefs,Path.Combine(folder,$"settings-{nav.ToLowerInvariant()}-{scale*100:0}.png"),scale);
                }
                checks["SettingsSave"]=prefs!.SaveForSmoke();prefs=null;
                // Capture transitions separately; rasterization must never contaminate benchmark frames.
                Fixture(3);
                foreach(var target in new[]{IslandState.Collapsed,IslandState.Minimal})
                {
                    window.SetState(IslandState.Expanded,false);await Task.Delay(80);window.SetState(target);
                    var watch=Stopwatch.StartNew();var frames=new List<object>();
                    foreach(var at in new[]{0,40,80,120,180,260,340})
                    {
                        await Task.Delay(Math.Max(1,at-(int)watch.ElapsedMilliseconds));
                        var actual=watch.Elapsed.TotalMilliseconds;
                        Raster(window,Path.Combine(folder,$"close-{target}-{at:D3}.png"),VisualTreeHelper.GetDpi(window).DpiScaleX);
                        var detail=(FrameworkElement)window.FindName("Details");var compact=(FrameworkElement)window.FindName("Compact");
                        frames.Add(new{RequestedMs=at,ActualMs=actual,DetailsOpacity=detail.Opacity,CompactOpacity=compact.Opacity,window.VisualWidth,window.VisualHeight});
                    }
                    await File.WriteAllTextAsync(Path.Combine(folder,$"close-{target}-frames.json"),JsonSerializer.Serialize(frames,new JsonSerializerOptions{WriteIndented=true}));
                }
                var state=new IslandInteraction();state.Set(IslandState.Expanded);state.Pointer(false,TimeSpan.Zero);
                checks["Leave899ms"]=!state.Pointer(false,TimeSpan.FromMilliseconds(899));checks["Leave900ms"]=state.Pointer(false,TimeSpan.FromMilliseconds(900))&&state.State==IslandState.Minimal;
                state.Set(IslandState.Expanded);state.Pointer(false,TimeSpan.Zero);state.Pointer(true,TimeSpan.FromMilliseconds(850));checks["ReenterCancels"]=!state.Pointer(true,TimeSpan.FromSeconds(2))&&state.State==IslandState.Expanded;
                await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,ActualNativeDpi=VisualTreeHelper.GetDpi(window).DpiScaleX,RasterScales=new[]{100,150,200},Data="Synthetic only"},new JsonSerializerOptions{WriteIndented=true}));
            }
            if(preview)
            {
                config.ShowCodex=config.ShowClaude=config.ShowDsh=config.ShowAntigravity=true;model.ApplyVisibility(config,today);Fixture(3);window.SetState(IslandState.Collapsed,false);
                // Opaque synthetic test canvas prevents capture of unrelated windows through the host.
                window.Background=new SolidColorBrush(Color.FromRgb(34,37,43));
                var inputTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};var lastInput="";
                inputTimer.Tick+=(_,_)=>{var value=$"{window.State}|{model.FocusLane??"combined"}|tip={window.TipVisible}";if(value==lastInput)return;lastInput=value;File.AppendAllText(Path.Combine(folder,"native-input.log"),DateTimeOffset.UtcNow.ToString("O")+" "+value+Environment.NewLine);};inputTimer.Start();
                window.PreviewMouseDown+=(_,_)=>File.AppendAllText(Path.Combine(folder,"native-input.log"),DateTimeOffset.UtcNow.ToString("O")+" MouseDown"+Environment.NewLine);
                window.PreviewKeyDown+=(_,e)=>{
                    if(e.Key==System.Windows.Input.Key.F6){window.SetState(IslandState.Collapsed,false);e.Handled=true;}
                    if(e.Key==System.Windows.Input.Key.F7){window.SetState(IslandState.Expanded);e.Handled=true;}
                    if(e.Key==System.Windows.Input.Key.F8){window.VerificationSuspendsPointer=!window.VerificationSuspendsPointer;e.Handled=true;}
                    if(e.Key==System.Windows.Input.Key.F9){model.SettingsCommand.Execute(null);e.Handled=true;}
                    if(e.Key is System.Windows.Input.Key.D1 or System.Windows.Input.Key.D2 or System.Windows.Input.Key.D3){Fixture(e.Key==System.Windows.Input.Key.D1?1:e.Key==System.Windows.Input.Key.D2?2:3);window.SetState(IslandState.Minimal,false);e.Handled=true;}
                };
                window.Closed+=(_,_)=>{inputTimer.Stop();Shutdown();};return;
            }
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(folder,"failure.txt"),ex.ToString());checks["ExceptionFree"]=false;}
        prefs?.Close();window?.Close();Shutdown(checks.Values.All(v=>v)?0:1);
    }
    private static ProviderViewModel? FindProvider(DependencyObject element)
    {
        for(var p=VisualTreeHelper.GetParent(element);p is not null;p=VisualTreeHelper.GetParent(p))if(p is FrameworkElement{DataContext:ProviderViewModel provider})return provider;
        return null;
    }
}
