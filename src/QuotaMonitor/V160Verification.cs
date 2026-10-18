using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.Security;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

namespace QuotaMonitor;
public partial class App
{
    private async Task VerifyAntigravityAsync()
    {
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","antigravity-v1.6.0");Directory.CreateDirectory(folder);
        var adapter=new AntigravityAdapter();var samples=new List<object>();var passed=true;
        for(var i=0;i<3;i++)
        {
            var s=await adapter.RefreshAsync(true,CancellationToken.None);
            passed&=s.ConnectionStatus==ConnectionStatus.Ready&&s.Windows.Count==2&&s.Windows.All(w=>w.ResetAt is not null);
            samples.Add(new{s.ConnectionStatus,s.LastUpdated,SourceUpdatedAt="Not provided by RPC",Weekly=s.Windows.Select(w=>new{w.Bucket,w.RemainingPercent,w.ResetAt}),s.ErrorMessage});
            if(i<2)await Task.Delay(15000);
        }
        await File.WriteAllTextAsync(Path.Combine(folder,"live-adapter.json"),JsonSerializer.Serialize(new{Passed=passed,Transport="Same-user PID-owned loopback HTTPS; no proxy/redirect",Samples=samples},new JsonSerializerOptions{WriteIndented=true}));
        Shutdown(passed?0:1);
    }

    // Synthetic UI regression. No production settings, account credentials, history or network are read.
    private async Task VerifyV160Async(bool preview)
    {
        var args=Environment.GetCommandLineArgs();
        var suffix=args.SkipWhile(a=>a!="--evidence-name").Skip(1).FirstOrDefault()??"gui";
        if(suffix.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'))suffix="gui";
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","antigravity-v1.6.0",suffix);Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();IslandWindow? window=null;SettingsWindow? prefs=null;
        try
        {
            var today=DateOnly.FromDateTime(DateTime.Now);var now=DateTimeOffset.UtcNow;
            var config=new MonitorSettings{HideInFullscreen=false,TopCenter=!preview,Left=0};var model=new MonitorViewModel();
            ProviderSnapshot Ag(int mode)=>new(){ProviderId="antigravity",DisplayName="Antigravity",MetricType=MetricType.Quota,ConnectionStatus=ConnectionStatus.Ready,LastUpdated=now,Windows=[new("gemini-weekly","Gemini",mode is 1 or 3?92:0,10080,now.AddDays(4)),new("3p-weekly","Claude/GPT",mode is 2 or 3?94:0,10080,now.AddDays(5))]};
            void Fixture(int mode)
            {
                model.Codex.Update(new(){ProviderId="codex",DisplayName="Codex",MetricType=MetricType.Quota,LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,Windows=[new("codex","Primary",90,300,now.AddHours(2)),new("codex","Secondary",92,10080,now.AddDays(2))],RemainingPercent=8});
                model.Claude.Update(new(){ProviderId="claude",DisplayName="ClaudeCN",LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,RemainingBalance=2.35m,Currency="USD"});
                model.Dsh.Update(new(){ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY","3.28")],[new("CNY","10.50")],now,"synthetic fixture"),DshBalanceState.Ready,null)});
                model.Antigravity.Update(Ag(mode));model.UpdateAlerts(config,now);
            }
            foreach(var id in UsageGrid.Order)model.Providers.Add(new(ProviderSnapshot.Empty(id,MonitorViewModel.NameOf(id),MetricType.Quota)));
            foreach(var (id,multiplier) in new[]{("codex",21000L),("claude",17000L),("dsh",32000L)})
            {
                var days=Enumerable.Range(0,175).Where(i=>(i+(id=="dsh"?2:0))%13!=0).Select(i=>new UsageDay(today.AddDays(-i),i%7==0?0:((i*37+(id=="claude"?17:0))%71)*multiplier,1,null,i%17==0?UsageQuality.Partial:UsageQuality.Exact,now,Excluded:i%17==0?1:0)).ToArray();
                model.UpdateHistory(new(){ProviderId=id,Days=days,Source="synthetic fixture",Scope="UI regression only"},today);
            }
            model.ApplyVisibility(config,today);Fixture(3);
            model.RefreshCommand=new ActionCommand(()=>Fixture(3));model.ExitCommand=new ActionCommand(()=>Shutdown());
            model.SettingsCommand=new ActionCommand(()=>{if(prefs is {IsVisible:true})return;prefs=new SettingsWindow(config,new SettingsStore(Path.Combine(folder,"settings")),new CredentialStore("QuotaMonitor/V160Synthetic/"+Environment.ProcessId),new HttpClient(),()=>model.ApplyVisibility(config,today),null,false){Owner=window};prefs.Show();});
            window=new IslandWindow(model,config,()=>{}){Title="QuotaMonitor v1.6.0 · Synthetic verification",ShowInTaskbar=preview,ShowActivated=preview};MainWindow=window;window.Show();window.VerificationSuspendsPointer=true;
            if(preview){NativeWindow.VerificationInspectable(new System.Windows.Interop.WindowInteropHelper(window).Handle);window.VerificationFixedInput=true;
                window.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.F8){window.VerificationSuspendsPointer=!window.VerificationSuspendsPointer;e.Handled=true;File.AppendAllText(Path.Combine(folder,"native-pointer.txt"),DateTimeOffset.UtcNow.ToString("o")+" pointer="+(!window.VerificationSuspendsPointer)+Environment.NewLine);}};
            }
            window.SetState(IslandState.Expanded,false);await Task.Delay(250);
            var dpi=VisualTreeHelper.GetDpi(window).DpiScaleX;
            var heatmap=(UsageHeatmap)window.FindName("Heatmap");
            checks["SquareCells"]=model.Grid!.Days.All(d=>Math.Abs(heatmap.Tile(d).Width-heatmap.Tile(d).Height)<.001);
            checks["SevenRows26Weeks"]=model.Grid.Weeks==26&&model.Grid.Days.All(d=>d.Row is >=0 and <7);
            checks["NoMainScroll"]=window.ScrollViewersInPanel==0;
            Capture(window,Path.Combine(folder,"expanded-fixture.png"));
            // Offscreen renders exercise vector rasterization at exact output scales; native scale is reported separately.
            foreach(var scale in new[]{1d,1.5,2})
            {
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(folder,$"expanded-{scale*100:0}-raster.png"));encoder.Save(stream);
                checks[$"Raster{scale*100:0}"]=bitmap.PixelWidth==(int)Math.Ceiling(window.ActualWidth*scale);
            }
            var originalHistories=JsonSerializer.Serialize(model.Histories);
            for(var mask=0;mask<16;mask++)
            {
                config.ShowCodex=(mask&1)>0;config.ShowClaude=(mask&2)>0;config.ShowDsh=(mask&4)>0;config.ShowAntigravity=(mask&8)>0;
                model.ApplyVisibility(config,today);window.SetState(IslandState.Expanded,false);await Task.Delay(35);window.UpdateLayout();
                var ids=UsageGrid.Order.Where(config.Visible).ToArray();
                checks[$"Mask{mask:D2}Data"]=ids.SequenceEqual(model.VisibleProviders.Select(p=>p.Id))&&ids.SequenceEqual(model.Grid!.Providers.Select(p=>p.Id))&&ids.SequenceEqual(model.Lanes.Select(p=>p.Id));
                var instruments=(ItemsControl)window.FindName("Instruments");
                checks[$"Mask{mask:D2}Visual"]=instruments.Items.Count==ids.Length&&((Button)FindVisual<Button>(window).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="Settings")).IsVisible;
                checks[$"Mask{mask:D2}Tip"]=model.BuildTip(model.Grid!.Days[^1]).Rows.Select(r=>r.Name).SequenceEqual(ids.Select(MonitorViewModel.NameOf));
                checks[$"Mask{mask:D2}Alert"]=model.Alerts.All(a=>a.Key.StartsWith("codex",StringComparison.Ordinal)?config.ShowCodex:a.Key.StartsWith("ag-",StringComparison.Ordinal)?config.ShowAntigravity:a.Key=="claude"?config.ShowClaude:config.ShowDsh);
                checks[$"Mask{mask:D2}NoHistoryLoss"]=originalHistories==JsonSerializer.Serialize(model.Histories);
                if(mask==0)checks["CompactEmptyExpanded"]=window.VisualWidth==420&&window.VisualHeight<180&&heatmap.Visibility==Visibility.Collapsed;
                if(mask is 0 or 7 or 15)Capture(window,Path.Combine(folder,$"mask-{mask:D2}-expanded-fixture.png"));
                window.SetState(IslandState.Collapsed,false);checks[$"Mask{mask:D2}Collapsed"]=((ItemsControl)FindVisual<ItemsControl>((FrameworkElement)window.FindName("Compact")).First()).Items.Count==ids.Length;
                if(mask is 0 or 15)Capture(window,Path.Combine(folder,$"mask-{mask:D2}-collapsed-fixture.png"));
            }
            config.ShowCodex=config.ShowClaude=config.ShowDsh=config.ShowAntigravity=true;model.ApplyVisibility(config,today);
            Fixture(0);window.SetState(IslandState.Collapsed,false);window.UpdateLayout();
            var compact=(FrameworkElement)window.FindName("Compact");
            checks["LongestWeeklyCollapsed"]=FindVisual<StackPanel>(compact).Where(p=>p.Orientation==Orientation.Horizontal&&p.DataContext is ProviderViewModel).All(p=>{
                var rect=p.TransformToVisual(compact).TransformBounds(new Rect(p.RenderSize));return rect.Left>=-10&&rect.Right<=compact.ActualWidth+10;
            });
            Capture(window,Path.Combine(folder,"collapsed-weekly-100-fixture.png"));Fixture(3);
            window.SetState(IslandState.Expanded,false);await Task.Delay(100);
            var height=window.VisualHeight;var bounds=window.WindowEvidence.ToString();
            foreach(var id in UsageGrid.Order)
            {
                model.HoverLane=id;await Task.Delay(200);window.UpdateLayout();
                checks["Hover"+id]=model.FocusLane==id&&heatmap.FocusLane==id&&model.BuildTip(model.Grid!.Days[^1]).Rows.Count==1;
                checks["HoverStable"+id]=window.VisualHeight==height&&window.WindowEvidence.ToString()==bounds;
                Capture(window,Path.Combine(folder,$"hover-{id}-fixture.png"));
                model.HoverLane=null;await Task.Delay(180);checks["Leave"+id]=heatmap.FocusLane is null&&model.FocusLane is null;
            }
            model.HoverLane="codex";window.HoverDayForSmoke(today);checks["SingleTip"]=model.Tip?.Rows.Count==1&&window.TipVisible;
            model.HoverLane=null;window.HoverDayForSmoke(today);checks["CombinedTip"]=model.Tip?.Rows.Count==4;
            Capture(window,Path.Combine(folder,"tooltip-fixture.png"));window.HoverDayForSmoke(DateOnly.MinValue);
            for(var mode=0;mode<4;mode++)
            {
                Fixture(mode);window.SetState(IslandState.Minimal,false);await Task.Delay(50);
                var ag=model.MinimalAlerts.Where(a=>a.Key=="antigravity").ToArray();
                checks["AntigravityAlert"+mode]=ag.Length==(mode==0?0:1)&&(mode!=3||ag[0].SecondValue is not null);
                var alert=(AlertReadout)window.FindName("AlertStrip");checks["AlertWidth"+mode]=alert.CapsuleWidth<=window.Width&&window.VisualHeight==26;
                Capture(window,Path.Combine(folder,$"alert-{mode}-fixture.png"));
            }
            model.Antigravity.Update(Ag(3) with{ConnectionStatus=ConnectionStatus.Stale});model.UpdateAlerts(config,now);checks["StaleNoAlert"]=model.Alerts.All(a=>!a.Key.StartsWith("ag-",StringComparison.Ordinal));
            Fixture(3);window.SetState(IslandState.Expanded,false);
            // Time-state regression independent of the desktop pointer: hover within the shell cancels leave deadlines.
            var state=new IslandInteraction();state.Set(IslandState.Expanded);state.Pointer(false,TimeSpan.Zero);state.Pointer(true,TimeSpan.FromMilliseconds(850));checks["HoverCancels900ms"]=!state.Pointer(true,TimeSpan.FromSeconds(2))&&state.State==IslandState.Expanded;
            state.Pointer(false,TimeSpan.FromSeconds(3));checks["Leave899ms"]=!state.Pointer(false,TimeSpan.FromMilliseconds(3899));checks["Leave900ms"]=state.Pointer(false,TimeSpan.FromMilliseconds(3900))&&state.State==IslandState.Minimal;
            // New checkbox/settings roundtrip uses an isolated store and unique empty credential target.
            model.SettingsCommand.Execute(null);await Task.Delay(100);
            foreach(var mask in Enumerable.Range(0,16))
            {
                var names=new[]{"Codex","Claude","Dsh","Antigravity"};
                for(var n=0;n<4;n++)((CheckBox)prefs!.FindName("Show"+names[n]+"Check")).IsChecked=(mask&(1<<n))!=0;
                prefs!.UpdateLayout();
                checks[$"SettingsMask{mask:D2}"]=names.Select((name,n)=>((RadioButton)prefs.FindName(name+"Tab")).Visibility==((mask&(1<<n))!=0?Visibility.Visible:Visibility.Collapsed)).All(v=>v);
            }
            var show=(CheckBox)prefs!.FindName("ShowAntigravityCheck");show.IsChecked=false;
            checks["SettingsCheckboxSave"]=prefs.SaveForSmoke()&&!config.ShowAntigravity&&!model.VisibleProviders.Any(p=>p.Id=="antigravity");
            model.SettingsCommand.Execute(null);await Task.Delay(100);checks["CheckboxReload"]=((CheckBox)prefs!.FindName("ShowAntigravityCheck")).IsChecked==false;
            ((CheckBox)prefs.FindName("ShowAntigravityCheck")).IsChecked=true;checks["CheckboxRestore"]=prefs.SaveForSmoke()&&model.VisibleProviders.Any(p=>p.Id=="antigravity");
            checks["HistoryRetained"]=originalHistories==JsonSerializer.Serialize(model.Histories);
            // Retain the established fixed-HWND Morph architecture while the four-provider strip opens/closes.
            window.RecordMotion=true;
            var frameBounds=new List<NativeWindow.Rect>();var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;
            foreach(var targetState in new[]{IslandState.Minimal,IslandState.Collapsed,IslandState.Expanded,IslandState.Collapsed,IslandState.Expanded})
            {
                window.SetState(targetState);await Task.Delay(380);NativeWindow.GetWindowRect(hwnd,out var frame);frameBounds.Add(frame);
            }
            window.RecordMotion=false;
            checks["MorphFixedHwnd"]=frameBounds.All(b=>b.Left==frameBounds[0].Left&&b.Top==frameBounds[0].Top&&b.Right==frameBounds[0].Right&&b.Bottom==frameBounds[0].Bottom);
            checks["MorphBounded"]=window.MotionSamples.All(s=>s.Width>=76&&s.Width<=IslandWindow.ExpandedWidth&&s.Height>=8&&s.Height<=480);
            var interiors=window.MotionSamples.Zip(window.MotionSamples.Skip(1)).Where(p=>p.Second.Ms-p.First.Ms<60&&p.First.Amount>.03&&p.First.Amount<.97).ToArray();
            checks["MorphNoFrameLayout"]=interiors.Length>0&&interiors.All(p=>p.First.Layouts==p.Second.Layouts&&p.First.NativePositions==p.Second.NativePositions);
            await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),ActualNativeDpi=dpi,Checks=checks,Fixture="Synthetic only; no production store, credentials or network",RasterScales=new[]{100,150,200}},new JsonSerializerOptions{WriteIndented=true}));
            if(preview){window.VerificationSuspendsPointer=true;window.SetState(IslandState.Expanded,false);window.Closed+=(_,_)=>Shutdown();return;}
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(folder,"failure.txt"),ex.ToString());checks["ExceptionFree"]=false;}
        prefs?.Close();window?.Close();Shutdown(checks.Count>0&&checks.Values.All(v=>v)?0:1);
    }
    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T item)yield return item;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in FindVisual<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
