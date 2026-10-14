using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

namespace QuotaMonitor;
public partial class App
{
    private async Task VerifyMorphAsync()
    {
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","interaction-v1.5.0","gui");Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();IslandWindow? window=null;Window? backdrop=null;
        var cursor=NativeWindow.Cursor();var samples=new List<object>();
        var source=StoreMigration.CandidateFolders(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","Local","QuotaMonitor")).OrderByDescending(p=>File.Exists(Path.Combine(p,"history.json"))).ThenByDescending(p=>File.GetLastWriteTimeUtc(Path.Combine(p,"history.json"))).FirstOrDefault(p=>File.Exists(Path.Combine(p,"snapshots.json")));
        string? Hash(string name)=>source is not null&&File.Exists(Path.Combine(source,name))?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,name)))):null;
        var before=StoreMigration.Files.ToDictionary(n=>n,Hash);
        try
        {
            var settings=new MonitorSettings{TopCenter=false,Left=210,Top=0,HideInFullscreen=false};var model=new MonitorViewModel();
            var snapshots=source is null?[]:JsonSerializer.Deserialize<ProviderSnapshot[]>(File.ReadAllText(Path.Combine(source,"snapshots.json")))??[];
            foreach(var id in UsageGrid.Order)model.Providers.Add(new(snapshots.FirstOrDefault(s=>s.ProviderId==id)??ProviderSnapshot.Empty(id,MonitorViewModel.NameOf(id),MetricType.Tokens)));
            if(source is not null&&File.Exists(Path.Combine(source,"history.json")))foreach(var history in JsonSerializer.Deserialize<UsageHistory[]>(File.ReadAllText(Path.Combine(source,"history.json")))??[])model.UpdateHistory(history,DateOnly.FromDateTime(DateTime.Now));
            model.Rebuild(DateOnly.FromDateTime(DateTime.Now));model.SettingsCommand=model.RefreshCommand=model.ExitCommand=new ActionCommand(()=>{});
            var preview=Environment.GetCommandLineArgs().Contains("--interaction-preview");var dockSaves=0;
            window=new IslandWindow(model,settings,()=>dockSaves++){Title="QuotaMonitor v1.5.0 Interaction verification",ShowInTaskbar=preview,ShowActivated=preview};MainWindow=window;window.Show();
            var hwnd=new WindowInteropHelper(window).Handle;var dpi=NativeWindow.Scale(hwnd);var area=NativeWindow.WorkArea(hwnd);
            NativeWindow.VerificationInspectable(hwnd);
            var backdropClicks=0;var backdropSurface=new Border{Background=new SolidColorBrush(Color.FromRgb(235,238,242))};backdropSurface.MouseLeftButtonUp+=(_,_)=>backdropClicks++;
            backdrop=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Content=backdropSurface,Width=window.Width,Height=window.Height};
            backdrop.Show();NativeWindow.Position(new WindowInteropHelper(backdrop).Handle,window.Width,window.Height,settings.TopCenter,settings.Left,settings.Top,0);window.Reposition();
            void Away()=>VerificationInput.Move(area.Left+30,area.Bottom-30);
            void Fixture(int mask)
            {
                var now=DateTimeOffset.UtcNow;
                model.Codex.Update(new(){ProviderId="codex",DisplayName="Codex",LastUpdated=now,ConnectionStatus=ConnectionStatus.Ready,RemainingPercent=(mask&1)>0?12:90,Windows=[new("codex","Primary",(mask&1)>0?88:10,300,now.AddHours(1)),new("codex","Secondary",(mask&2)>0?92:10,10080,now.AddDays(1))]});
                model.Claude.Update(new(){ProviderId="claude",DisplayName="Claude",LastUpdated=now,ConnectionStatus=ConnectionStatus.Partial,RemainingBalance=(mask&4)>0?.52m:100,Currency="USD"});
                model.Dsh.Update(new(){ProviderId="dsh",DisplayName="DSH",ConnectionStatus=ConnectionStatus.Partial,AccountBalance=new(new([new("CNY",(mask&8)>0?".80":"100")],[],now,"verification fixture"),DshBalanceState.Ready,null)});
                model.UpdateAlerts(settings,now);
            }
            // Warm up templates, layout, glyphs and the cached shadow before timing.
            Away();window.VerificationSuspendsPointer=true;Fixture(0);window.SetState(IslandState.Expanded,false);await Task.Delay(350);
            DesktopCapture(window,Path.Combine(folder,"expanded-real-history-fixture-balances.png"));
            checks["HeatmapRetained"]=((FrameworkElement)window.FindName("Heatmap")).ActualHeight>100&&window.ScrollViewersInPanel==0;
            window.SetState(IslandState.Collapsed,false);await Task.Delay(150);DesktopCapture(window,Path.Combine(folder,"collapsed-fixture.png"));
            window.SetState(IslandState.Minimal,false);await Task.Delay(150);
            DesktopCapture(window,Path.Combine(folder,"minimal-desktop.png"));
            // Manual inspection runs the same actual window with isolated fixture readouts.
            if(Environment.GetCommandLineArgs().Contains("--interaction-preview"))
            {
                backdrop.Close();backdrop=null;
                Fixture(Environment.GetCommandLineArgs().Contains("--preview-alert")?15:0);
                window.SetState(IslandState.Minimal,false);window.VerificationSuspendsPointer=false;
                window.Closed+=(_,_)=>Shutdown();return;
            }
            var host=new List<NativeWindow.Rect>();window.RecordMotion=true;
            for(var cycle=0;cycle<12;cycle++)
            {
                window.SetState(IslandState.Collapsed);await Task.Delay(400);NativeWindow.GetWindowRect(hwnd,out var b);host.Add(b);
                window.SetState(IslandState.Minimal);await Task.Delay(400);NativeWindow.GetWindowRect(hwnd,out b);host.Add(b);
            }
            window.RecordMotion=false;
            var performance=window.MotionSamples.ToArray();window.MotionSamples.Clear();
            checks["FixedHwndAcrossMorph"]=host.All(b=>b.Left==host[0].Left&&b.Top==host[0].Top&&b.Right==host[0].Right&&b.Bottom==host[0].Bottom);
            // Ignore target changes; test the interior of each continuous segment for layout/native churn.
            var interiors=performance.Zip(performance.Skip(1)).Where(p=>p.Second.Ms-p.First.Ms<60&&p.First.Amount>0.03&&p.First.Amount<.97).ToArray();
            checks["NoLayoutInMotionInterior"]=interiors.All(p=>p.First.Layouts==p.Second.Layouts);
            checks["NoNativePositionInMotion"]=performance.Select(s=>s.NativePositions).Distinct().Count()==1;
            checks["CenterStableToSubpixel"]=performance.All(s=>Math.Abs(s.X+(s.Width-24*s.Amount)/2-window.Width/2)<.00001);
            checks["BoundedNoSizeOvershoot"]=performance.All(s=>s.Width>=176-.0001&&s.Width<=520.0001&&s.Height>=8-.0001&&s.Height<=82.0001);
            var timed=performance.Where(s=>s.FrameMs>0&&s.FrameMs<100).Select(s=>s.FrameMs).Order().ToArray();
            double Percentile(double[] values,double p)=>values.Length==0?0:values[(int)Math.Min(values.Length-1,Math.Floor((values.Length-1)*p))];
            await File.WriteAllTextAsync(Path.Combine(folder,"performance.json"),JsonSerializer.Serialize(new{ActualDpi=dpi,RenderTier=RenderCapability.Tier>>16,Samples=performance,P50=Percentile(timed,.5),P95=Percentile(timed,.95),P99=Percentile(timed,.99),Over25=timed.Count(t=>t>25),Over33=timed.Count(t=>t>33),CpuP95=Percentile(performance.Select(s=>s.CpuMs).Order().ToArray(),.95)},new JsonSerializerOptions{WriteIndented=true}));
            // All 16 threshold combinations, including both Codex windows and partial balances.
            foreach(var mask in Enumerable.Range(0,16))
            {
                Fixture(mask);await Task.Delay(400);var count=((mask&3)>0?1:0)+((mask&4)>0?1:0)+((mask&8)>0?1:0);
                checks[$"Combination{mask:D2}"]=model.MinimalAlerts.Count==count&&Math.Abs(window.VisualHeight-(count==0?8:26))<.001;
                if((mask&3)==3)checks[$"WeekPriority{mask:D2}"]=model.MinimalAlerts.Any(a=>a.Key=="codex-week"&&a.Value=="7d 8%")&&!model.MinimalAlerts.Any(a=>a.Key=="codex-5h");
                DesktopCapture(window,Path.Combine(folder,$"alerts-{mask:D2}-fixture.png"));
            }
            Fixture(15);await Task.Delay(400);
            foreach(var provider in model.Providers)provider.Update(provider.Snapshot with{LastUpdated=DateTimeOffset.UtcNow.AddMinutes(-8),AccountBalance=provider.Snapshot.AccountBalance is {} balance?balance with{State=DshBalanceState.Stale}:null});
            model.UpdateAlerts(settings);await Task.Delay(400);checks["StaleClearsAndRestores8Dip"]=model.MinimalAlerts.Count==0&&window.VisualHeight==8;
            // No encoding, rendering-to-bitmap, or UpdateLayout on the UI thread during this desktop sequence.
            NativeWindow.GetWindowRect(hwnd,out var rect);var recording=new List<(double Ms,System.Drawing.Bitmap Bitmap)>();var timer=Stopwatch.StartNew();
            var capture=Task.Run(async()=>
            {
                var next=0d;
                while(timer.Elapsed.TotalSeconds<5.4)
                {
                    var bitmap=new System.Drawing.Bitmap(rect.Right-rect.Left,Math.Min(rect.Bottom-rect.Top,(int)(180*dpi)));
                    using(var g=System.Drawing.Graphics.FromImage(bitmap))g.CopyFromScreen(rect.Left,rect.Top,0,0,bitmap.Size);
                    recording.Add((timer.Elapsed.TotalMilliseconds,bitmap));next+=1000d/60;
                    var delay=next-timer.Elapsed.TotalMilliseconds;if(delay>0)await Task.Delay(TimeSpan.FromMilliseconds(delay));
                }
            });
            window.VerificationSuspendsPointer=false;window.RecordMotion=true;
            Fixture(0);await Task.Delay(180);
            var center=(rect.Left+rect.Right)/2;VerificationInput.Move(center,rect.Top+(int)(5*dpi));await Task.Delay(600);
            checks["RealHoverOpensCollapsed"]=window.State==IslandState.Collapsed;
            VerificationInput.Move(center,rect.Top+(int)(40*dpi));await Task.Delay(80);VerificationInput.Click();await Task.Delay(400);checks["RealClickOpensExpanded"]=window.IsExpanded;
            Away();await Task.Delay(1350);checks["RealLeaveReturnsMinimal"]=window.State==IslandState.Minimal;
            Fixture(1);await Task.Delay(400);Fixture(5);await Task.Delay(400);Fixture(15);await Task.Delay(400);
            VerificationInput.Move(center,rect.Top+(int)(10*dpi));await Task.Delay(200);Away();await Task.Delay(850);
            VerificationInput.Move(center,rect.Top+(int)(10*dpi));await Task.Delay(200);Away();await capture;
            window.VerificationSuspendsPointer=true;window.RecordMotion=false;await Task.Delay(450);
            var sequence=Path.Combine(folder,"desktop-sequence");Directory.CreateDirectory(sequence);var manifest=new List<object>();
            for(var i=0;i<recording.Count;i++){var item=recording[i];var path=Path.Combine(sequence,$"{i:D4}.png");item.Bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);manifest.Add(new{Index=i,Ms=item.Ms,Image=$"{i:D4}.png"});item.Bitmap.Dispose();}
            await File.WriteAllTextAsync(Path.Combine(sequence,"frames.json"),JsonSerializer.Serialize(new{Capture="actual desktop GDI on background thread; 60 Hz request, no UI-thread bitmap capture",Dpi=dpi,Rect=rect,Frames=manifest,Motion=window.MotionSamples},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
            // Verify actual native clicks across the entire silhouette, including the empty margins.
            var interactions=new List<object>();
            async Task MoveLocal(Point point){var screen=window.PointToScreen(point);VerificationInput.Move((int)Math.Round(screen.X),(int)Math.Round(screen.Y));await Task.Delay(80);}
            foreach(var fraction in new[]{.01,.06,.25,.5,.75,.94,.99})foreach(var y in new[]{.2,.5,.8})
            {
                Fixture(0);window.VerificationSuspendsPointer=true;window.SetState(IslandState.Collapsed,false);await Task.Delay(50);
                var b=window.VisibleBounds;var p=new Point(b.Left+b.Width*fraction,b.Top+b.Height*y);
                if(!window.ShellContains(p))continue;
                await MoveLocal(p);var injected=VerificationInput.Click();await Task.Delay(340);
                checks[$"WholeCapsuleClick-{fraction}-{y}"]=injected&&window.IsExpanded;
                interactions.Add(new{Case="native shell click",X=fraction,Y=y,Injected=injected,State=window.State.ToString()});
            }
            foreach(var mask in new[]{0,15})
            {
                Fixture(mask);window.SetState(IslandState.Expanded,false);await Task.Delay(80);var b=window.VisibleBounds;
                var inside=new Point(b.Left+b.Width/2,b.Bottom-30);await MoveLocal(inside);
                window.VerificationSuspendsPointer=false;
                Away();await Task.Delay(450);checks[$"ExpandedLeaveGrace-{mask}"]=window.IsExpanded;
                await MoveLocal(inside);await Task.Delay(620);checks[$"ExpandedReentryCancelsDeadline-{mask}"]=window.IsExpanded;
                // Return after the deadline, while geometry is still moving. Restore directly to Expanded.
                Away();await Task.Delay(995);checks[$"ExpandedExitTargetsBase-{mask}"]=window.State==IslandState.Minimal;
                await MoveLocal(inside);await Task.Delay(350);checks[$"ExpandedReentryReversesMorph-{mask}"]=window.IsExpanded;
                for(var i=0;i<10;i++){Away();await Task.Delay(35);await MoveLocal(inside);}
                checks[$"RapidPointerEdges-{mask}"]=window.IsExpanded;
                Away();await Task.Delay(1350);
                checks[$"ExpandedSettlesAtCorrectBase-{mask}"]=window.State==IslandState.Minimal&&Math.Abs(window.VisualHeight-(mask==0?8:26))<.001;
                DesktopCapture(window,Path.Combine(folder,mask==0?"minimal-after-exit.png":"alert-minimal-after-exit.png"));
                interactions.Add(new{Case="expanded exit",Mask=mask,GraceMs=900,State=window.State.ToString(),Height=window.VisualHeight});
                window.VerificationSuspendsPointer=true;
            }
            // Owned Settings-style dialogs and menus reset the deadline instead of expiring underneath.
            Fixture(0);window.SetState(IslandState.Expanded,false);
            var owned=new Window{Owner=window,Width=260,Height=100,ShowInTaskbar=false,Title="Interaction suspension probe"};owned.Show();
            window.VerificationSuspendsPointer=false;Away();await Task.Delay(1100);checks["OwnedDialogSuspendsExit"]=window.IsExpanded;owned.Close();
            await Task.Delay(450);checks["DialogCloseRestartsGrace"]=window.IsExpanded;await Task.Delay(900);checks["DialogCloseReturnsMinimal"]=window.State==IslandState.Minimal;
            window.VerificationSuspendsPointer=true;window.SetState(IslandState.Collapsed,false);
            var releaseBounds=window.VisibleBounds;
            await MoveLocal(new Point(releaseBounds.Left+2,releaseBounds.Top+releaseBounds.Height/2));VerificationInput.Down();await Task.Delay(50);
            await MoveLocal(new Point(releaseBounds.Left-2,releaseBounds.Top+releaseBounds.Height/2));VerificationInput.Up();await Task.Delay(80);
            window.VerificationSuspendsPointer=false;Away();await Task.Delay(1100);checks["OutsideMouseReleaseDoesNotStrandState"]=window.State==IslandState.Minimal;
            window.VerificationSuspendsPointer=true;
            foreach(var incoming in new[]{0,15})
            {
                Fixture(incoming==0?15:0);window.SetState(IslandState.Expanded,false);Fixture(incoming);
                window.VerificationSuspendsPointer=false;Away();await Task.Delay(1350);
                checks[$"AlertsChangedWhileExpandedReturnCurrentBase-{incoming}"]=window.State==IslandState.Minimal&&window.VisualHeight==(incoming==0?8:26);
                window.VerificationSuspendsPointer=true;
            }
            Fixture(0);window.SetState(IslandState.Collapsed,false);await Task.Delay(80);var dragBounds=window.VisibleBounds;
            var dragStart=window.PointToScreen(new Point(dragBounds.Left+40,dragBounds.Top+dragBounds.Height/2));
            await MoveLocal(new Point(dragBounds.Left+40,dragBounds.Top+dragBounds.Height/2));
            NativeWindow.GetWindowRect(hwnd,out var beforeDrag);
            await Task.Run(async()=>{VerificationInput.Down();await Task.Delay(60);for(var i=1;i<=12;i++){VerificationInput.Move((int)dragStart.X+i*12,(int)dragStart.Y);await Task.Delay(25);}VerificationInput.Up();});
            await Task.Delay(120);NativeWindow.GetWindowRect(hwnd,out var afterDrag);
            checks["WholeCapsuleDragStillPersistsDock"]=dockSaves>0&&afterDrag.Left>beforeDrag.Left+60&&!settings.TopCenter;
            settings.Left=210;settings.Top=0;window.Reposition();window.SetState(IslandState.Minimal,false);
            await File.WriteAllTextAsync(Path.Combine(folder,"interaction-cases.json"),JsonSerializer.Serialize(interactions,new JsonSerializerOptions{WriteIndented=true}));
            Fixture(15);window.SetState(IslandState.Minimal,false);
            // Stress retargeting with snapshots before/after each requested target, including expansion.
            var jump=false;for(var i=0;i<36;i++){var w=window.VisualWidth;var h=window.VisualHeight;window.SetState((IslandState)(i%3));jump|=Math.Abs(w-window.VisualWidth)>4||Math.Abs(h-window.VisualHeight)>4;await Task.Delay(28);}
            window.SetState(IslandState.Minimal);await Task.Delay(450);checks["RapidRetargetSettlesWithoutSnap"]=!jump&&window.State==IslandState.Minimal&&window.VisualHeight==26;
            // Dynamic alpha contours at requested raster DPIs, explicitly separate from native timing.
            var alpha=new List<object>();
            foreach(var scale in new[]{1d,1.5,2})foreach(var count in new[]{0,1,2,3})
            {
                Fixture(count switch{0=>0,1=>1,2=>5,_=>15});window.SetState(IslandState.Minimal,false);
                foreach(var state in new[]{IslandState.Minimal,IslandState.Collapsed,IslandState.Expanded})
                {
                    window.SetState(state,false);await Task.Delay(60);var bitmap=AlphaRender(window,scale);var pixels=InspectAlpha(window,bitmap,scale);
                    WritePng(bitmap,Path.Combine(folder,$"dpi-{scale*100:0}-{count}-{state}.png"));
                    checks[$"Alpha{scale*100:0}-{count}-{state}"]=pixels.Center>=250&&pixels.OpaqueOutside==0;
                    // Physical raster hit coordinates must map back to the same DIP silhouette and sensor.
                    var bounds=window.VisibleBounds;
                    checks[$"HitRegion{scale*100:0}-{count}-{state}"]=new[]{.1,.5,.9}.All(x=>window.ShellContains(new Point(Math.Round((bounds.Left+bounds.Width*x)*scale)/scale,Math.Round((bounds.Top+bounds.Height/2)*scale)/scale)))&&!window.ShellContains(new Point(bounds.Left-2,bounds.Top+bounds.Height/2))&&IslandWindow.Near(new Point(bounds.Right+11,bounds.Bottom+15),bounds,state)&&!IslandWindow.Near(new Point(bounds.Right+25,bounds.Bottom+25),bounds,state);
                    alpha.Add(new{Scale=scale,Count=count,State=state.ToString(),pixels.Center,pixels.OpaqueOutside,pixels.ShadowPixels});
                }
            }
            await File.WriteAllTextAsync(Path.Combine(folder,"dpi-raster.json"),JsonSerializer.Serialize(alpha,new JsonSerializerOptions{WriteIndented=true}));
            var dynamicDpi=new List<object>();
            foreach(var scale in new[]{1d,1.5,2})
            {
                Fixture(0);window.SetState(IslandState.Minimal,false);
                foreach(var target in new[]{IslandState.Collapsed,IslandState.Minimal})
                {
                    window.SetState(target);
                    for(var i=0;i<12;i++)
                    {
                        await Task.Delay(24);var bitmap=AlphaRender(window,scale);var pixels=InspectAlpha(window,bitmap,scale);
                        WritePng(bitmap,Path.Combine(folder,$"dynamic-{scale*100:0}-{target}-{i:D2}.png"));
                        dynamicDpi.Add(new{Scale=scale,Target=target.ToString(),Index=i,Pose=window.MorphEvidence,pixels.OpaqueOutside});
                        checks[$"DynamicContour{scale*100:0}-{target}-{i:D2}"]=pixels.OpaqueOutside==0;
                    }
                    await Task.Delay(120);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(folder,"dynamic-dpi-raster.json"),JsonSerializer.Serialize(dynamicDpi,new JsonSerializerOptions{WriteIndented=true}));
            foreach(var state in new[]{IslandState.Collapsed,IslandState.Expanded})
            {
                window.SetState(state,false);await Task.Delay(100);var b=window.VisibleBounds;var previous=backdropClicks;
                foreach(var point in new[]{new Point(b.Left+1,b.Top+1),new Point(b.Left-6,b.Top+b.Height/2),new Point(5,300)})
                {
                    var screen=window.PointToScreen(point);VerificationInput.Move((int)screen.X,(int)screen.Y);await Task.Delay(90);VerificationInput.Click();await Task.Delay(50);
                }
                checks[$"{state}CornerShadowAndEmptyHostPassThrough"]=backdropClicks==previous+3;
            }
            checks["PinControlsRemoved"]=window.FindName("PinButton") is null&&window.FindName("RailPin") is null;
            checks["ProductionSettingsAndSnapshotsUntouched"]=before.Where(p=>p.Key!="history.json").All(p=>p.Value==Hash(p.Key));
            Fixture(15);window.SetState(IslandState.Minimal,false);window.VerificationSuspendsPointer=false;
            await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,ActualDpi=dpi,Host=window.WindowEvidence,Before=before,After=StoreMigration.Files.ToDictionary(n=>n,Hash),Data="fixtures for balances and alerts; read-only real cached history; no adapters or credential operations",NativeDpiCoverage=new[]{dpi},RasterDpiCoverage=new[]{1d,1.5,2}},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
            if(Environment.GetCommandLineArgs().Contains("--hold")){await Task.Delay(45000);}
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(folder,"failure.txt"),ex.ToString());checks["ExceptionFree"]=false;}
        finally{if(!Environment.GetCommandLineArgs().Contains("--interaction-preview")){backdrop?.Close();window?.Close();VerificationInput.Move(cursor.X,cursor.Y);Shutdown(checks.Count>0&&checks.Values.All(v=>v)?0:1);}}
    }
}
