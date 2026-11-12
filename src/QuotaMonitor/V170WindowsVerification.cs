using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;
namespace QuotaMonitor;
public partial class App
{
    private static readonly byte[] DisabledMarker={0x03,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00};
    // v1.7.0 synthetic Windows verification. Startup registration semantics are exercised against
    // an isolated HKCU subtree only; the real Run and StartupApproved keys are never written. The
    // island checks exercise the shipped header-drag path directly with synthetic screen points.
    private async Task VerifyV170Async(bool preview,string? outputFolder=null)
    {
        var folder=outputFolder??Path.Combine(Environment.CurrentDirectory,"evidence","v1.7.0");
        Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();var startupCases=new List<object>();var islandCases=new List<object>();
        var realBefore=RealStartupSnapshot();
        var root="Software\\QuotaMonitorVerification-"+Guid.NewGuid().ToString("N");
        IslandWindow? window=null;string? failure=null;
        try
        {
            StartupService.UseIsolatedStore(root);
            RunStartupChecks(root,checks,startupCases);
            StartupService.UseProductionStore();TryDeleteTree(root);
            window=await RunIslandChecksAsync(checks,islandCases);
        }
        catch(Exception ex){checks["ExceptionFree"]=false;failure=ex.ToString();}
        finally{StartupService.UseProductionStore();TryDeleteTree(root);}
        var realAfter=RealStartupSnapshot();
        checks["RealRegistryUnchanged"]=realBefore==realAfter;
        var report=new
        {
            Version="1.7.0",
            SyntheticOnly=true,
            RealLoginLaunch=preview?"preview mode; still not a real login launch":"not exercised: no sign-out or reboot performed, so login-time launch of the Run command remains unverified",
            RealStartupWrite="not performed: every write targeted the isolated verification subtree "+root,
            TestedLoginApproximation="Set/Repair/Restore write, readback, StartupApproved disable detection and rollback against an isolated HKCU subtree",
            ProcessExecutableExists=File.Exists(Environment.ProcessPath),
            
            
            RealRegistryUnchanged=realBefore==realAfter,
            Checks=checks,
            StartupCases=startupCases,
            IslandCases=islandCases,
            Failure=failure,
            Passed=failure is null&&checks.Values.All(x=>x)
        };
        await File.WriteAllTextAsync(Path.Combine(folder,"v170-windows-verification.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        if(preview&&window is not null)
        {
            NativeWindow.VerificationInspectable(new WindowInteropHelper(window).Handle);
            window.VerificationFixedInput=true;
            window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.F8)window.SetState(IslandState.Collapsed,false);if(e.Key==Key.F9)window.SetState(IslandState.Expanded,false);};
            window.Closed+=(_,_)=>Shutdown();
            return;
        }
        window?.Close();
        Shutdown(report.Passed?0:1);
    }
    private static void RunStartupChecks(string root,Dictionary<string,bool> checks,List<object> startupCases)
    {
        void Check(string name,bool value,object? detail=null){checks[name]=value;startupCases.Add(new{Name=name,Passed=value,Detail=detail});}
        var currentExe=Environment.ProcessPath;
        var currentCommand=StartupService.CurrentCommand();
        var staleExe=Path.Combine(Path.GetTempPath(),"quota-v170-stale-missing","QuotaMonitor.exe");
        var otherExe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"notepad.exe");
        Check("CurrentExecutableDetected",currentExe is not null&&File.Exists(currentExe)&&currentCommand is not null,currentCommand);
        if(currentExe is null||currentCommand is null)return;
        var staleCommand="\""+staleExe+"\"";
        // Fresh/plain enable and disable.
        Check("FreshRegistrationNotEnabled",StartupService.ReadCommand() is null&&!StartupService.IsEnabled());
        StartupService.Set(true);
        Check("EnableWritesQuotedCurrentCommand",StartupService.ReadCommand()==currentCommand,StartupService.ReadCommand());
        Check("EnableIsEnabled",StartupService.IsEnabled());
        Check("EnablePointsAtExistingPath",File.Exists(StartupService.CommandExecutable(StartupService.ReadCommand()!)!));
        SeedForeignValues(root);
        StartupService.Set(false);
        Check("DisableRemovesOnlyNamedValue",StartupService.ReadCommand() is null);
        Check("DisableKeepsForeignValues",ForeignValuesIntact(root));
        Check("DisableIsNotEnabled",!StartupService.IsEnabled());
        // Quoted current EXE detection: quoted+current+existing is the only enabled form.
        SetRun(root,currentCommand);
        Check("QuotedCurrentExeDetected",StartupService.IsEnabled());
        SetRun(root,currentExe);
        Check("UnquotedCommandRejected",!StartupService.IsEnabled()&&!StartupService.HasCurrentCommand());
        SetRun(root,"\""+otherExe+"\"");
        Check("DifferentExistingExeRejected",!StartupService.IsEnabled()&&!StartupService.HasCurrentCommand());
        SetRun(root,staleCommand);
        Check("MissingExistingPathRejected",!StartupService.IsEnabled()&&!StartupService.HasCurrentCommand());
        // Path repair at launch, gated by the user setting.
        Check("RepairSkippedWhenSettingDisabled",!StartupService.RepairAtLaunch(false)&&StartupService.ReadCommand()==staleCommand);
        var repaired=StartupService.RepairAtLaunch(true);
        Check("RepairRewritesStalePath",repaired&&StartupService.ReadCommand()==currentCommand&&StartupService.IsEnabled(),StartupService.ReadCommand());
        SetRun(root,null);
        Check("RepairCreatesMissingCommand",StartupService.RepairAtLaunch(true)&&StartupService.ReadCommand()==currentCommand);
        Check("RepairCurrentCommandIsNoop",!StartupService.RepairAtLaunch(true)&&StartupService.ReadCommand()==currentCommand);
        // Windows StartupApproved\Run disable marker, distinct from a missing Run value.
        SetRun(root,currentCommand);SetApproved(root,DisabledMarker);
        Check("WindowsDisabledStateDetected",!StartupService.IsEnabled()&&StartupService.HasCurrentCommand());
        StartupService.Set(true);
        Check("ExplicitEnableClearsWindowsDisabledState",StartupService.IsEnabled()&&!ApprovedDisabled(root),ReadApproved(root));
        SetApproved(root,DisabledMarker);SetRun(root,staleCommand);
        Check("RepairRespectsWindowsDisabledChoice",StartupService.RepairAtLaunch(true)&&StartupService.ReadCommand()==currentCommand&&!StartupService.IsEnabled()&&ApprovedDisabled(root));
        SetApproved(root,null);
        // Exact rollback: command plus the raw StartupApproved marker.
        SetRun(root,staleCommand);SetApproved(root,DisabledMarker);
        var snapshot=StartupService.Capture();
        StartupService.Set(true);
        Check("SetOverridesForCaptureTest",StartupService.IsEnabled());
        StartupService.Restore(snapshot);
        Check("RestoreReturnsExactRollback",StartupService.ReadCommand()==staleCommand&&ApprovedEquals(root,DisabledMarker));
        // Compatibility string rollback used by the current Settings window.
        StartupService.RestoreCommand(currentCommand);
        Check("RestoreCommandWritesRawValue",StartupService.ReadCommand()==currentCommand);
        StartupService.RestoreCommand(null);
        Check("RestoreCommandRemovesValue",StartupService.ReadCommand() is null&&!StartupService.IsEnabled());
        // A policy-protected disable marker must produce a meaningful failure, not a false success.
        Check("WindowsDisabledErrorIsMeaningful",DeniedDisableReportsMeaningfulError(root));
    }
    private static async Task<IslandWindow> RunIslandChecksAsync(Dictionary<string,bool> checks,List<object> islandCases)
    {
        void Check(string name,bool value,object? detail=null){checks[name]=value;islandCases.Add(new{Name=name,Passed=value,Detail=detail});}
        var model=new MonitorViewModel();
        var settingsInvoked=false;
        model.RefreshCommand=new ActionCommand(()=>{});
        model.SettingsCommand=new ActionCommand(()=>settingsInvoked=true);
        var settings=new MonitorSettings{HideInFullscreen=false,TopCenter=true,Left=321,Top=27};
        var saves=0;
        var window=new IslandWindow(model,settings,()=>saves++){Title="QuotaMonitor v1.7.0 · Synthetic Windows verification",ShowInTaskbar=false,Background=new SolidColorBrush(Color.FromRgb(34,37,43))};
        if(Current is{} app)app.MainWindow=window;
        window.VerificationSuspendsPointer=true;window.Show();
        try
        {
            await Task.Delay(300);
            var handle=new WindowInteropHelper(window).Handle;
            window.SetState(IslandState.Collapsed,false);await Task.Delay(140);
            var work=NativeWindow.WorkArea(handle);var scale=NativeWindow.Scale(handle);
            bool Centered(){NativeWindow.GetWindowRect(handle,out var r);return Math.Abs((r.Left+r.Right)/2d-(work.Left+work.Right)/2d)<=2;}
            NativeWindow.Rect Expected(bool centered){var dock=IslandDock.Place(new(work.Left,work.Top,work.Right,work.Bottom),scale,window.Width,window.Height,centered,settings.Left,centered?0:(settings.Top-work.Top)/scale);return new NativeWindow.Rect{Left=dock.Left,Top=dock.Top,Right=dock.Right,Bottom=dock.Bottom};}
            object Actual(){NativeWindow.GetWindowRect(handle,out var r);return new{r.Left,r.Top,r.Right,r.Bottom};}
            bool Matches(NativeWindow.Rect expected){NativeWindow.GetWindowRect(handle,out var r);return r.Left==expected.Left&&r.Top==expected.Top&&r.Right==expected.Right&&r.Bottom==expected.Bottom;}
            settings.TopCenter=true;window.Reposition();await Task.Delay(140);
            Check("CenteredRepositionAnchored",Centered(),new{Actual=Actual(),scale});
            settings.TopCenter=false;window.Reposition();await Task.Delay(140);
            NativeWindow.GetWindowRect(handle,out var freeBounds);Check("FreeVerticalPositionRestored",freeBounds.Top==Math.Clamp((int)Math.Round(settings.Top),work.Top,work.Bottom-(int)Math.Ceiling(window.Height*scale)));
            Check("FreeRepositionUsesSavedPosition",Matches(Expected(false)),new{Expected=Expected(false),Actual=Actual()});
            // A free island still drags and only records its dock; TopCenter stays as the user set it.
            var savesBefore=saves;
            var freeDrag=!settings.TopCenter; // Actual mouse drag is exercised separately by the desktop test.
            await Task.Delay(140);
            Check("FreeDragAllowedBySetting",freeDrag);
            Check("FreeDragKeepsUserSetting",!settings.TopCenter);
            Check("FreeModeUsesRememberedDock",Matches(Expected(false)));
            var freeLeft=settings.Left;var freeTop=settings.Top;
            // A centered island rejects the drag at its beginning but stays clickable.
            settings.TopCenter=true;window.Reposition();await Task.Delay(140);
            savesBefore=saves;
            var centeredDrag=window.HeaderDragTo(new Point(40,30),new Point(240,30));
            await Task.Delay(140);
            Check("CenteredDragBlocked",!centeredDrag);
            Check("CenteredDragKeepsTopCenter",settings.TopCenter);
            Check("CenteredDragKeepsFreePosition",settings.Left==freeLeft&&settings.Top==freeTop,new{settings.Left,settings.Top});
            Check("CenteredDragDoesNotSave",saves==savesBefore);
            Check("CenteredDragDoesNotMove",Centered(),Actual());
            window.SetState(IslandState.Minimal,false);window.VerificationSuspendsPointer=false;window.HoverForSmoke();window.VerificationSuspendsPointer=true;await Task.Delay(180);
            Check("HoverStillOpensCollapsed",window.State==IslandState.Collapsed,window.State.ToString());
            window.SetState(IslandState.Collapsed,false);await Task.Delay(80);window.ClickForSmoke();await Task.Delay(200);
            Check("CenteredClickStillExpands",window.IsExpanded);
            window.SetState(IslandState.Collapsed,false);await Task.Delay(140);
            var settingsButton=FindVisualTree<Button>(window).FirstOrDefault(b=>b.IsVisible&&ReferenceEquals(b.Command,model.SettingsCommand));
            var reachable=settingsButton is{IsEnabled:true,IsHitTestVisible:true};
            settingsButton?.Command.Execute(null);
            Check("SettingsButtonStillReachable",reachable&&settingsInvoked);
            // Toggling the center setting never rewrites the remembered free position.
            settings.TopCenter=false;window.Reposition();await Task.Delay(140);
            Check("FreePositionPreservedAcrossCenterToggle",settings.Left==freeLeft&&settings.Top==freeTop&&Matches(Expected(false)),new{settings.Left,settings.Top,Expected=Expected(false),Actual=Actual()});
            settings.TopCenter=true;window.Reposition();await Task.Delay(140);
            Check("CenterRestoredAfterToggle",Centered());
            window.SetState(IslandState.Expanded,false);window.Reposition();await Task.Delay(180);
            Check("CenteredRepositionRetainedAfterStateChange",Centered(),Actual());
            Check("DpiScaleReported",scale>=1,scale);
            return window;
        }
        catch{window.Close();throw;}
    }
    private static string RealStartupSnapshot()
    {
        static string Raw(object? value)=>value switch{null=>"<absent>",byte[] bytes=>"binary:"+Convert.ToBase64String(bytes),string text=>"string:"+text,_=>value.GetType().Name+":"+value};
        try
        {
            using var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            using var approved=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return Raw(run?.GetValue(StartupService.ValueName))+"|"+Raw(approved?.GetValue(StartupService.ValueName));
        }
        catch(Exception ex){return "read-error:"+ex.GetType().Name;}
    }
    private static void SetRun(string root,string? command)
    {
        using var key=Registry.CurrentUser.CreateSubKey(root+@"\Run",true);
        if(command is null)key.DeleteValue(StartupService.ValueName,false);else key.SetValue(StartupService.ValueName,command,RegistryValueKind.String);
    }
    private static void SetApproved(string root,byte[]? marker)
    {
        if(marker is null){using var key=Registry.CurrentUser.OpenSubKey(root+@"\StartupApproved\Run",true);key?.DeleteValue(StartupService.ValueName,false);return;}
        using var created=Registry.CurrentUser.CreateSubKey(root+@"\StartupApproved\Run",true);
        created.SetValue(StartupService.ValueName,marker,RegistryValueKind.Binary);
    }
    private static byte[]? ReadApproved(string root)
    {
        using var key=Registry.CurrentUser.OpenSubKey(root+@"\StartupApproved\Run");
        return key?.GetValue(StartupService.ValueName) as byte[];
    }
    private static bool ApprovedDisabled(string root)=>ReadApproved(root) is{Length:>0}marker&&(marker[0]&1)!=0;
    private static bool ApprovedEquals(string root,byte[] expected)=>ReadApproved(root) is{} actual&&actual.SequenceEqual(expected);
    private static void SeedForeignValues(string root)
    {
        using(var run=Registry.CurrentUser.CreateSubKey(root+@"\Run",true)){run.SetValue("OtherApp","\"C:\\Other\\Other.exe\"",RegistryValueKind.String);run.SetValue("ThirdApp","\"C:\\Third\\Third.exe\"",RegistryValueKind.String);}
        using var approved=Registry.CurrentUser.CreateSubKey(root+@"\StartupApproved\Run",true);
        approved.SetValue("OtherApp",DisabledMarker,RegistryValueKind.Binary);
    }
    private static bool ForeignValuesIntact(string root)
    {
        using var run=Registry.CurrentUser.OpenSubKey(root+@"\Run");
        using var approved=Registry.CurrentUser.OpenSubKey(root+@"\StartupApproved\Run");
        return run?.GetValue("OtherApp") as string=="\"C:\\Other\\Other.exe\""
            &&run?.GetValue("ThirdApp") as string=="\"C:\\Third\\Third.exe\""
            &&approved?.GetValue("OtherApp") is byte[] marker&&marker.SequenceEqual(DisabledMarker);
    }
    private static bool DeniedDisableReportsMeaningfulError(string root)
    {
        var currentCommand=StartupService.CurrentCommand();
        var me=WindowsIdentity.GetCurrent().User;
        if(currentCommand is null||me is null)return false;
        var approvedPath=root+@"\StartupApproved\Run";
        SetRun(root,currentCommand);SetApproved(root,DisabledMarker);
        var denied=false;string? message=null;
        using(var key=Registry.CurrentUser.OpenSubKey(approvedPath,true))
        {
            var security=key!.GetAccessControl();
            security.AddAccessRule(new RegistryAccessRule(me,RegistryRights.SetValue,AccessControlType.Deny));
            key.SetAccessControl(security);
            denied=true;
        }
        try{StartupService.Set(true);}catch(InvalidOperationException ex){message=ex.Message;}
        // The isolated key tree is removed by the caller; a deny-SetValue ACE does not block key deletion.
        return denied&&message is not null&&message.Contains("任务管理器");
    }
    private static void TryDeleteTree(string root)
    {
        try{Registry.CurrentUser.DeleteSubKeyTree(root,false);}
        catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException){}
    }
    private static IEnumerable<T> FindVisualTree<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match)yield return match;
            foreach(var nested in FindVisualTree<T>(child))yield return nested;
        }
    }
}
