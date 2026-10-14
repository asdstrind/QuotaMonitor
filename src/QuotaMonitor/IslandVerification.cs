using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.Security;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

namespace QuotaMonitor;

// Explicit verification mode only: isolated app settings and cached aggregates, no adapters or Harness access.
public partial class App
{
    private void StartIslandVerification()
    {
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        instance=new Mutex(true,"Local\\QuotaMonitor.IslandVerification",out var created);
        if(!created){Shutdown(2);return;}
        _=VerifyIslandAsync();
    }

    private async Task VerifyIslandAsync()
    {
        var polish=Environment.GetCommandLineArgs().Contains("--verify-ui-polish");
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence",polish?"ui-polish-v1.3.1":"island-v3","ui");Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();var states=new Dictionary<string,object>();
        var originalCursor=NativeWindow.Cursor();IslandWindow? window=null;Window? probe=null;SettingsWindow? form=null;
        var source=StoreMigration.CandidateFolders(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .OrderByDescending(p=>File.Exists(Path.Combine(p,"history.json"))).ThenByDescending(p=>File.GetLastWriteTimeUtc(Path.Combine(p,"history.json"))).FirstOrDefault(p=>File.Exists(Path.Combine(p,"snapshots.json")));
        string? Hash(string name)=>source is not null&&File.Exists(Path.Combine(source,name))?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,name)))):null;
        var before=StoreMigration.Files.ToDictionary(n=>n,Hash);
        T Read<T>(string name,T fallback)=>source is not null&&File.Exists(Path.Combine(source,name))?JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(source,name)))??fallback:fallback;
        try
        {
            var config=Read("settings.json",new MonitorSettings());config.TopCenter=true;config.HideInFullscreen=true;
            var model=new MonitorViewModel();foreach(var id in UsageGrid.Order){var snapshot=Read<ProviderSnapshot[]>("snapshots.json",[]).FirstOrDefault(p=>p.ProviderId==id)??ProviderSnapshot.Empty(id,MonitorViewModel.NameOf(id),MetricType.Tokens);model.Providers.Add(new(snapshot));}
            foreach(var historyData in Read<UsageHistory[]>("history.json",[]))model.UpdateHistory(historyData,DateOnly.FromDateTime(DateTime.Now));
            model.Rebuild(DateOnly.FromDateTime(DateTime.Now));model.SetScale(config.HeatmapScale=="relative");model.UpdateAlerts(config);
            model.RefreshCommand=new ActionCommand(()=>{});model.SettingsCommand=new ActionCommand(()=>{});model.ExitCommand=new ActionCommand(()=>{});
            var dockStore=new SettingsStore(Path.Combine(folder,"dock-roundtrip"));var saves=0;
            window=new IslandWindow(model,config,()=>{dockStore.Save(config);saves++;});window.Show();
            var hwnd=new WindowInteropHelper(window).Handle;
            if(polish&&Environment.GetCommandLineArgs().Contains("--edge-only"))
            {
                window.VerificationSuspendsPointer=true;var edgeResults=new List<object>();
                foreach(var state in new[]{IslandState.Collapsed,IslandState.Expanded})
                {
                    window.SetState(state,false);await Task.Delay(180);var bitmap=AlphaRender(window,1.5);var pixels=InspectAlpha(window,bitmap,1.5);
                    WritePng(bitmap,Path.Combine(folder,$"edge-only-{state}.png"));
                    checks[state.ToString()]=pixels.EdgeAlpha==0;edgeResults.Add(new{State=state.ToString(),pixels.EdgeAlpha});
                }
                await File.WriteAllTextAsync(Path.Combine(folder,"edge-only.json"),JsonSerializer.Serialize(edgeResults));return;
            }
            checks["NativeTopmost"]=NativeWindow.IsTopmost(hwnd);
            async Task Pause(int ms=400)=>await Task.Delay(ms);
            async Task Save(string name){await Pause(160);states[name]=window.WindowEvidence;DesktopCapture(window,Path.Combine(folder,name+".png"));Capture(window,Path.Combine(folder,name+"-render.png"));}
            void MoveAway(){var area=NativeWindow.WorkArea(hwnd);VerificationInput.Move(area.Left+50,area.Bottom-50);}
            MoveAway();await Pause(1200);
            checks["DefaultMinimal"]=window.State==IslandState.Minimal;await Save("minimal");
            NativeWindow.GetWindowRect(hwnd,out var minimalBounds);var scale=NativeWindow.Scale(hwnd);
            checks["MinimalDimensions"]=minimalBounds.Right-minimalBounds.Left==(int)Math.Ceiling(176*scale)&&minimalBounds.Bottom-minimalBounds.Top==(int)Math.Ceiling(8*scale);
            checks["FlushToTop"]=minimalBounds.Top==NativeWindow.WorkArea(hwnd).Top;
            VerificationInput.Move((minimalBounds.Left+minimalBounds.Right)/2,minimalBounds.Bottom+(int)(8*scale));
            await Pause(80);checks["ApproachDelay"]=window.State==IslandState.Minimal;
            var frames=new List<NativeWindow.Rect>();for(var i=0;i<40;i++){NativeWindow.GetWindowRect(hwnd,out var frame);frames.Add(frame);await Pause(16);}
            checks["ApproachAnimationKeepsCenter"]=frames.All(f=>Math.Abs(f.Left+f.Right-minimalBounds.Left-minimalBounds.Right)<=2);
            checks["ApproachAnimationHasIntermediateSizes"]=!SystemParameters.ClientAreaAnimation||frames.Any(f=>f.Bottom-f.Top>12&&f.Bottom-f.Top<123);
            checks["NativeHoverOnlyCollapsed"]=window.State==IslandState.Collapsed&&!window.IsExpanded;await Save("collapsed");
            await Pause(900);checks["HoverNeverExpands"]=window.State==IslandState.Collapsed;
            NativeWindow.GetWindowRect(hwnd,out var collapsedBounds);
            var clickX=collapsedBounds.Left+(int)(80*scale);var clickY=collapsedBounds.Top+(int)(40*scale);
            VerificationInput.Move(clickX,clickY);await Pause(150);
            checks["ClickTargetsActualWindow"]=VerificationInput.WindowAt(clickX,clickY)==hwnd;
            checks["NativeClickInjected"]=VerificationInput.Click();await Pause(900);
            checks["NativeClickExpanded"]=window.State==IslandState.Expanded;await Save("expanded");
            checks["NoPanelScrollbars"]=window.ScrollViewersInPanel==0;
            if(polish)await VerifyPolishAsync(window,folder,checks);
            var day=model.Grid?.Days.Where(d=>d.Lanes.Any(l=>l.HasUsage)).OrderByDescending(d=>d.Date).FirstOrDefault();
            if(day is not null){window.HoverDayForSmoke(day.Date);checks["HeatmapTooltip"]=window.TipVisible;await Save("expanded-tooltip");window.HoverDayForSmoke(DateOnly.MinValue);}
            MoveAway();await Pause(250);checks["LeaveGrace"]=window.State==IslandState.Expanded;
            await Pause(1100);checks["NativeLeaveReturnsMinimal"]=window.State==IslandState.Minimal;
            // A brief approach must not inflate the island.
            NativeWindow.GetWindowRect(hwnd,out minimalBounds);VerificationInput.Move((minimalBounds.Left+minimalBounds.Right)/2,minimalBounds.Bottom+5);await Pause(60);MoveAway();await Pause(500);
            checks["BriefApproachIgnored"]=window.State==IslandState.Minimal;
            checks["PinControlsRemoved"]=window.FindName("PinButton") is null&&window.FindName("RailPin") is null;
            // Settings validation and save operate on an isolated store and an unused credential target.
            window.SetState(IslandState.Expanded,false);
            var isolated=new SettingsStore(Path.Combine(folder,"isolated-settings"));
            using var client=new HttpClient();
            form=new SettingsWindow(config,isolated,new CredentialStore("QuotaMonitor/IslandVerification/"+Guid.NewGuid()),client,()=>model.UpdateAlerts(config)){Owner=window};form.Show();await Pause(300);
            DesktopCapture(form,Path.Combine(folder,"settings.png"));
            ((TextBox)form.FindName("FiveHourThreshold")).Text="NaN";
            checks["InvalidThresholdRejected"]=!form.SaveForSmoke()&&form.IsVisible;
            ((TextBox)form.FindName("FiveHourThreshold")).Text="23";((TextBox)form.FindName("WeeklyThreshold")).Text="12";((TextBox)form.FindName("ClaudeThreshold")).Text="3.5";((TextBox)form.FindName("DshThreshold")).Text="4.5";
            checks["SettingsSave"]=form.SaveForSmoke();
            var loaded=isolated.Load();checks["SettingsThresholdRoundtrip"]=loaded.CodexFiveHourLowPercent==23&&loaded.CodexWeeklyLowPercent==12&&loaded.ClaudeLowBalanceUsd==3.5m&&loaded.DshLowBalance==4.5m;
            form=null;MoveAway();await Pause(1300);
            // Click below the 8 DIP strip: an underlying real HWND receives it, proving the sensor does not intercept.
            window.SetState(IslandState.Minimal,false);NativeWindow.GetWindowRect(hwnd,out minimalBounds);
            var received=0;var button=new Button{Content="UI verification"};button.Click+=(_,_)=>received++;
            probe=new Window{Width=240,Height=100,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Content=button};probe.Show();
            NativeWindow.Position(new WindowInteropHelper(probe).Handle,240,100,true,0,0,0);
            var underX=(minimalBounds.Left+minimalBounds.Right)/2;var underY=minimalBounds.Bottom+(int)(4*scale);
            VerificationInput.Move(underX,underY);checks["BelowStripClickInjected"]=VerificationInput.Click();await Pause(70);
            checks["BelowStripClickPassesThrough"]=received==1;probe.Close();probe=null;MoveAway();await Pause(1100);
            // Actual foreground fullscreen window, then restore. No game/exclusive-fullscreen claim.
            var area=NativeWindow.WorkArea(hwnd);
            probe=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Background=System.Windows.Media.Brushes.Black};probe.Show();probe.WindowState=WindowState.Maximized;probe.Activate();await Pause(2500);
            checks["ForegroundFullscreenDetected"]=NativeWindow.IsFullscreen(hwnd);
            checks["FullscreenHiddenAndNonInteractive"]=window.Opacity==0&&!window.IsHitTestVisible;
            probe.Close();probe=null;await Pause(2500);checks["FullscreenRestoreMinimal"]=window.Opacity==1&&window.IsHitTestVisible&&window.State==IslandState.Minimal;
            // Native drag persists the horizontal dock and the selected monitor; recreate the window from disk.
            window.SetState(IslandState.Collapsed,false);NativeWindow.GetWindowRect(hwnd,out var dragStart);
            var dragX=dragStart.Left+(int)(80*scale);var dragY=dragStart.Top+(int)(40*scale);
            VerificationInput.Move(dragX,dragY);await Pause(100);
            await Task.Run(async()=>{VerificationInput.Down();await Task.Delay(100);for(var i=1;i<=12;i++){VerificationInput.Move(dragX+i*15,dragY);await Task.Delay(25);}VerificationInput.Up();});
            await Pause(350);checks["NativeDragPersistsDock"]=saves>0&&!config.TopCenter&&File.Exists(Path.Combine(dockStore.Root,"settings.json"));
            // 700ms leave grace + 360ms morph + pointer/render scheduling; sample the settled endpoint.
            MoveAway();await Pause(1400);NativeWindow.GetWindowRect(hwnd,out var docked);
            window.Hide();var restarted=new IslandWindow(model,dockStore.Load(),()=>{});restarted.Show();await Pause(300);NativeWindow.GetWindowRect(new WindowInteropHelper(restarted).Handle,out var restored);
            checks["DockRestoredAfterWindowRecreation"]=docked.Left==restored.Left&&docked.Top==restored.Top&&restarted.State==IslandState.Minimal;
            states["dockBeforeRestart"]=docked;states["dockAfterRestart"]=restored;
            restarted.Close();window.Show();config.TopCenter=true;window.Reposition();await Pause(200);
            // Explicit fixture preview in this isolated verification window only; production data never replaced.
            var now=DateTimeOffset.UtcNow;foreach(var p in model.Providers)
            {
                var fixture=p.Snapshot with{ConnectionStatus=ConnectionStatus.Ready,LastUpdated=now,NeedsSetup=false,IsEstimated=false};
                fixture=p.Id switch{"codex"=>fixture with{Windows=[new("codex","Primary",99,300,now.AddHours(1)),new("codex","Secondary",99,10080,now.AddDays(1))]},"claude"=>fixture with{RemainingBalance=1,Currency="USD"},_=>fixture with{AccountBalance=new(new([new("CNY","1")],[],now,"verification fixture"),DshBalanceState.Ready,null)}};
                p.Update(fixture);
            }
            model.UpdateAlerts(config,now);window.SetState(IslandState.Minimal,false);await Save("minimal-alerts-fixture");
            checks["FourAlertBindings"]=model.Alerts.Count==4&&model.FiveHourMark=="5h"&&model.WeeklyMark=="W"&&model.ClaudeMark=="C"&&model.DshMark=="D";
            checks["AlertsKeepMinimalSize"]=window.Width==176&&window.Height==8;
            foreach(var p in model.Providers)p.Update(p.Snapshot with{LastUpdated=now.AddMinutes(-8),AccountBalance=p.Snapshot.AccountBalance is {} b?b with{State=DshBalanceState.Stale}:null});
            model.UpdateAlerts(config,now);checks["StaleClearsAllAlertBindings"]=model.Alerts.Count==0;
            checks["ProductionStoreUntouched"]=before.All(pair=>pair.Value==Hash(pair.Key));
            await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),ProcessId=Environment.ProcessId,Checks=checks,States=states,CacheSource=source,DataMode="read-only real cached aggregates; only minimal-alerts-fixture uses synthetic balances",HarnessAccess=false,AdaptersStarted=false,Before=before,After=StoreMigration.Files.ToDictionary(n=>n,Hash)},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(folder,"failure.txt"),ex.ToString());checks["ExceptionFree"]=false;}
        finally{form?.Close();probe?.Close();window?.Close();VerificationInput.Move(originalCursor.X,originalCursor.Y);Shutdown(checks.Count>0&&checks.Values.All(v=>v)?0:1);}
    }
    private static void DesktopCapture(Window window,string path)
    {
        window.UpdateLayout();var hwnd=new WindowInteropHelper(window).Handle;NativeWindow.GetWindowRect(hwnd,out var r);
        using var bitmap=new System.Drawing.Bitmap(r.Right-r.Left,r.Bottom-r.Top);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.CopyFromScreen(r.Left,r.Top,0,0,bitmap.Size);
        bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
    }
}

internal static class VerificationInput
{
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Mouse{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public Mouse Mouse;}
    [DllImport("user32")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32")]private static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32")]private static extern IntPtr WindowFromPoint(Point point);
    public static void Move(int x,int y)=>SetCursorPos(x,y);
    public static IntPtr WindowAt(int x,int y)=>WindowFromPoint(new(){X=x,Y=y});
    public static bool Click()=>SendInput(2,[new(){Mouse=new(){Flags=2}},new(){Mouse=new(){Flags=4}}],Marshal.SizeOf<Input>())==2;
    public static void Down()=>SendInput(1,[new(){Mouse=new(){Flags=2}}],Marshal.SizeOf<Input>());
    public static void Up()=>SendInput(1,[new(){Mouse=new(){Flags=4}}],Marshal.SizeOf<Input>());
}
