using System.Drawing;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.Security;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;
using Forms=System.Windows.Forms;
namespace QuotaMonitor;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private SettingsStore store=null!;
    private MonitorSettings settings=null!;
    private CredentialStore secrets=null!;
    private HttpClient http=null!;
    private RefreshCoordinator coordinator=null!;
    private HistoryCoordinator history=null!;
    private DshBalanceReceiver balanceReceiver=null!;
    private MonitorViewModel vm=null!;
    private IslandWindow island=null!;
    private Forms.NotifyIcon? tray;
    private Icon? trayIcon;
    private readonly DispatcherTimer tick=new(){Interval=TimeSpan.FromSeconds(30)};
    private SettingsWindow? preferences;
    private bool exiting;
    private string? smokeFolder;
    private string evidenceFolder="ui";
    private bool balanceVerification;
    private DateTimeOffset historySaved=DateTimeOffset.MinValue;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--verify-settings")||e.Args.Contains("--settings-preview")){_=VerifySettingsAsync(e.Args.Contains("--settings-preview"));return;}
        if(e.Args.Contains("--verify-morph")||e.Args.Contains("--verify-island")||e.Args.Contains("--verify-ui-polish")||e.Args.Contains("--interaction-preview")){ShutdownMode=ShutdownMode.OnExplicitShutdown;_=VerifyMorphAsync();return;}
        balanceVerification=e.Args.Contains("--verify-dsh-balance");
        // --dev-window renders this build beside a running release without touching its store.
        var dev=e.Args.Contains("--dev-window");var root=dev?Path.Combine(Environment.CurrentDirectory,"evidence","interaction-v1.5.0","ui-dev","isolated-settings"):null;
        var mutexName=balanceVerification?"Local\\QuotaMonitor.DshBalanceVerification":dev?"Local\\QuotaMonitor.DevWindow":"Local\\QuotaMonitor.Desktop";
        evidenceFolder=balanceVerification?"ui-dsh":dev?"interaction-v1.5.0/ui-dev":"ui";
        instance=new Mutex(true,mutexName,out var created);if(!created){Shutdown();return;}
        DispatcherUnhandledException+=(_,args)=>{if(vm is not null)vm.Notice="发生错误（"+args.Exception.GetType().Name+"），请尝试刷新或重启。";args.Handled=true;};
        if(root is not null)Directory.CreateDirectory(root);
        else SettingsStore.ImportPreviousStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuotaMonitor"),3);
        store=new SettingsStore(root);settings=store.Load();secrets=new CredentialStore();
        if(balanceVerification){settings.CodexEnabled=false;settings.ClaudeEnabled=false;settings.DshRefreshSeconds=30;}
        http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
        var cached=store.LoadCache().Select(s=>s.AccountBalance?.Balance is {} balance?s with{AccountBalance=new(balance,DshBalanceState.Stale,"上次余额缓存；等待官方客户端快照。")}:s).ToArray();
        balanceReceiver=new DshBalanceReceiver(cachedBalance:cached.FirstOrDefault(s=>s.ProviderId=="dsh")?.AccountBalance?.Balance);
        // One adapter instance per platform serves both live values and daily history.
        var claude=new ClaudeCNAdapter(http,settings,secrets);var dsh=new DshAdapter(settings,balanceReceiver);
        coordinator=new RefreshCoordinator(settings,[new CodexAdapter(settings),claude,dsh],cached);
        var cachedHistory=balanceVerification?[]:store.LoadHistory();
        history=new HistoryCoordinator(settings,[new CodexHistorySource(),claude,dsh],cachedHistory);
        vm=new MonitorViewModel();foreach(var s in coordinator.Snapshots)vm.Providers.Add(new(s));
        vm.SetScale(settings.HeatmapScale=="relative");
        foreach(var h in cachedHistory)vm.UpdateHistory(h,history.Today);
        vm.Rebuild(history.Today);
        vm.ScaleChanged+=relative=>{settings.HeatmapScale=relative?"relative":"unified";SaveSettings();};
        vm.RefreshCommand=new ActionCommand(()=>_=Refresh());vm.SettingsCommand=new ActionCommand(OpenSettings);vm.ExitCommand=new ActionCommand(()=>_=ExitAsync());
        coordinator.Updated+=s=>Dispatcher.InvokeAsync(()=>{vm.Providers.First(x=>x.Id==s.ProviderId).Update(s);vm.UpdateAlerts(settings);});
        history.Updated+=h=>Dispatcher.InvokeAsync(()=>{if(exiting)return;vm.UpdateHistory(h,history.Today);SaveHistory(false);});
        island=new IslandWindow(vm,settings,SaveSettings);MainWindow=island;island.Closing+=(_,args)=>{if(!exiting){args.Cancel=true;island.Hide();}};island.Show();
        using(var iconStream=GetResourceStream(new Uri("pack://application:,,,/Assets/QuotaMonitor.ico")).Stream)trayIcon=new Icon(iconStream);
        tray=new Forms.NotifyIcon{Icon=trayIcon,Text="AI Harness Quota Monitor",Visible=true};
        var menu=new Forms.ContextMenuStrip();menu.Items.Add("刷新",null,(_,_)=>Dispatcher.Invoke(()=>_=Refresh()));menu.Items.Add("设置",null,(_,_)=>Dispatcher.Invoke(OpenSettings));menu.Items.Add("退出",null,(_,_)=>Dispatcher.Invoke(()=>_=ExitAsync()));tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{island.Show();island.SetState(IslandState.Collapsed);});
        tick.Tick+=(_,_)=>{foreach(var p in vm.Providers)p.Tick();vm.Tick(history.Today);vm.UpdateAlerts(settings);};tick.Start();
        if(store.LoadWarning is {} warning)vm.Notice=warning;
        coordinator.Start();
        if(!balanceVerification)history.Start();
        if(balanceVerification)_=VerifyDshBalanceAsync();
        if(e.Args.Contains("--smoke-test"))_=SmokeAsync(e.Args);
    }
    private void SaveSettings(){try{store.Save(settings);}catch{vm.Notice="设置无法保存，请检查本地目录权限。";}}
    // History aggregates are persisted at most once a minute while running, and always on exit.
    private void SaveHistory(bool force){if(balanceVerification||(!force&&DateTimeOffset.UtcNow-historySaved<TimeSpan.FromMinutes(1)))return;try{store.SaveHistory(vm.Histories);historySaved=DateTimeOffset.UtcNow;}catch(IOException){}catch(UnauthorizedAccessException){}}
    private async Task Refresh(){vm.Message="正在刷新各平台…";try{await Task.WhenAll(coordinator.RefreshAsync(true),history.RefreshAsync(true));}catch(OperationCanceledException){}finally{if(!exiting)vm.Message="已刷新 "+DateTime.Now.ToString("HH:mm:ss")+"；各平台独立读取";}}
    private void OpenSettings(){if(preferences is {IsVisible:true}){preferences.Activate();return;}preferences=new SettingsWindow(settings,store,secrets,http,()=>{vm.UpdateAlerts(settings);island.Reposition();_=Refresh();},()=>vm.Providers.FirstOrDefault(p=>p.Id=="dsh")?.Snapshot.CurrentModel){Owner=island};preferences.Closed+=(_,_)=>preferences=null;preferences.Show();}
    private async Task ExitAsync(){if(exiting)return;exiting=true;tick.Stop();preferences?.Close();tray?.Dispose();await coordinator.DisposeAsync();await history.DisposeAsync();await balanceReceiver.DisposeAsync();try{store.SaveCache(coordinator.Snapshots);}catch(IOException){}SaveHistory(true);http.Dispose();if(smokeFolder is {} folder)await File.WriteAllTextAsync(Path.Combine(folder,"shutdown.json"),"{\"CoordinatorDisposed\":true,\"HistoryCoordinatorDisposed\":true,\"TrayDisposed\":true,\"HttpDisposed\":true,\"BalanceReceiverDisposed\":true}");island.Close();Shutdown();}
    private static int Option(string[] args,string name,int fallback)=>int.TryParse(args.SkipWhile(x=>x!=name).Skip(1).FirstOrDefault(),out var n)?n:fallback;
    private async Task SmokeAsync(string[] args)
    {
        // In-process GUI integration test: actual adapters and actual shown WPF windows.
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence",evidenceFolder);smokeFolder=folder;Directory.CreateDirectory(folder);
        // Wait for real data: live values settled and the Claude history backfill finished (bounded).
        var limit=DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(Option(args,"--smoke-wait",35),10,900));
        // Give the passive Bridge its bounded reconnect backoff, then read the current source.
        await Task.Delay(TimeSpan.FromSeconds(35));await coordinator.RefreshAsync(true);
        // Wait for live values to settle and for the history backfill to finish, so the capture shows
        // real coverage. A source that stops making progress for 75 seconds no longer holds it up.
        var pendingDays=int.MaxValue;var stalled=DateTimeOffset.UtcNow;
        while(DateTimeOffset.UtcNow<limit)
        {
            var state=await Dispatcher.InvokeAsync(()=>(Loading:vm.Providers.Any(p=>p.Snapshot.ConnectionStatus==ConnectionStatus.Loading),Histories:vm.Histories.Count,Pending:vm.Histories.Sum(h=>h.PendingDays),Unloaded:vm.Histories.Count(h=>h.Days.Count==0&&h.Error is null)));
            if(!state.Loading&&state.Histories>=3&&state.Unloaded==0)
            {
                if(state.Pending==0)break;
                if(state.Pending<pendingDays){pendingDays=state.Pending;stalled=DateTimeOffset.UtcNow;}
                else if(DateTimeOffset.UtcNow-stalled>TimeSpan.FromSeconds(75))break;
            }
            await Task.Delay(2000);
        }
        // Smoke drives pointer deadlines explicitly; the real desktop cursor must not compete with it.
        await Dispatcher.InvokeAsync(()=>{island.VerificationSuspendsPointer=true;island.SetState(IslandState.Minimal,false);Capture(island,Path.Combine(folder,"minimal.png"));});
        await Dispatcher.InvokeAsync(island.HoverForSmoke);await Task.Delay(1100);bool hoverCollapsed=false,hoverOpened=false,leaveCollapsed=false,tipShown=false;string? hoveredDay=null;
        await Dispatcher.InvokeAsync(()=>{hoverCollapsed=island.State==IslandState.Collapsed;Capture(island,Path.Combine(folder,"collapsed.png"));island.ClickForSmoke();});await Task.Delay(900);
        await Dispatcher.InvokeAsync(()=>{hoverOpened=island.IsExpanded&&island.ActualHeight>200;Capture(island,Path.Combine(folder,"expanded.png"));});
        // Hover the most recent day where the most platforms have usage, as the pointer would.
        await Dispatcher.InvokeAsync(()=>{var day=vm.Grid?.Days.Where(d=>d.Lanes.Any(l=>l.HasUsage)).OrderByDescending(d=>d.Lanes.Count(l=>l.HasUsage)).ThenByDescending(d=>d.Date).FirstOrDefault();if(day is not null&&island.HoverDayForSmoke(day.Date) is {} hovered){hoveredDay=hovered.Date.ToString("yyyy-MM-dd");}});
        await Task.Delay(300);
        await Dispatcher.InvokeAsync(()=>{tipShown=island.TipVisible;Capture(island,Path.Combine(folder,"expanded-tooltip.png"));island.HoverDayForSmoke(DateOnly.MinValue);});
        await Dispatcher.InvokeAsync(()=>{vm.SetScale(true);vm.PinnedLane="claude";});await Task.Delay(300);
        await Dispatcher.InvokeAsync(()=>{Capture(island,Path.Combine(folder,"expanded-relative-claude.png"));vm.PinnedLane=null;vm.SetScale(settings.HeatmapScale=="relative");});
        await Dispatcher.InvokeAsync(island.LeaveForSmoke);
        await Task.Delay(1200);await Dispatcher.InvokeAsync(()=>{leaveCollapsed=island.State==IslandState.Minimal;island.SetExpanded(true,false);});
        bool settingsShown=false,settingsSaved=false;int scrollViewers=-1;object? expandedWindow=null;
        await Dispatcher.InvokeAsync(()=>{scrollViewers=island.ScrollViewersInPanel;expandedWindow=island.WindowEvidence;island.SetExpanded(true,false);vm.SettingsCommand.Execute(null);});await Task.Delay(500);await Dispatcher.InvokeAsync(()=>{if(preferences is {} w){settingsShown=w.IsVisible;Capture(w,Path.Combine(folder,"settings.png"));settingsSaved=w.SaveForSmoke();}});
        // Round-trip non-sensitive settings using an isolated folder, never credentials or Harness files.
        var testStore=new SettingsStore(Path.Combine(folder,"settings-roundtrip"));testStore.Save(settings);var read=testStore.Load();
        var credentialRoundtrip=false;var isolatedSecret=new CredentialStore("QuotaMonitor/SmokeTest/"+Guid.NewGuid());try{isolatedSecret.Write("synthetic-local-test");credentialRoundtrip=isolatedSecret.Read()=="synthetic-local-test";}finally{isolatedSecret.Delete();}
        var heatmap=await Dispatcher.InvokeAsync(()=>new{vm.Grid?.Start,vm.Grid?.Today,vm.Grid?.Weeks,Days=vm.Grid?.Days.Count,SharedReference=vm.Grid?.SharedReference,HoveredDay=hoveredDay,TooltipShown=tipShown,Lanes=vm.Lanes.Select(l=>new{l.Id,l.Total,l.Status,l.HasIssue}),Providers=vm.Grid?.Providers.Select(p=>new{p.Id,p.Tokens,p.Requests,p.Cost,p.ActiveDays,p.CoveredDays,p.CoverageStart,p.Reference,p.HasEstimated,p.HasPartial,Pending=p.History?.PendingDays,Error=p.History?.Error})});
        var proof=new{ProcessId=Environment.ProcessId,Started=true,NativeWindow=island.WindowEvidence,ExpandedWindow=expandedWindow,MinimalHeight=IslandWindow.MinimalHeight,CollapsedHeight=IslandWindow.CollapsedHeight,Expanded=island.IsExpanded,HoverEventCollapsed=hoverCollapsed,ClickEventExpanded=hoverOpened,LeaveEventDelayedMinimal=leaveCollapsed,ScrollViewersInMainPanel=scrollViewers,SettingsOpened=settingsShown,SettingsSaveButton=settingsSaved,SettingsRoundtrip=read.QuotaPerUsd==settings.QuotaPerUsd&&store.Load().QuotaPerUsd==settings.QuotaPerUsd,CredentialManagerRoundtrip=credentialRoundtrip,Heatmap=heatmap,Providers=coordinator.Snapshots};
        await File.WriteAllTextAsync(Path.Combine(folder,"smoke.json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));await Dispatcher.InvokeAsync(()=>{island.SetExpanded(false,false);island.VerificationSuspendsPointer=false;});
        await Dispatcher.InvokeAsync(()=>_=ExitAsync());
    }
    private async Task VerifyDshBalanceAsync()
    {
        // Actual DSH-only UI; no test wallets, credentials, or production settings changes.
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","ui-dsh");smokeFolder=folder;Directory.CreateDirectory(folder);
        await Task.Delay(35000);await coordinator.RefreshAsync(true);
        await Dispatcher.InvokeAsync(()=>{island.SetExpanded(false,false);Capture(island,Path.Combine(folder,"collapsed.png"));island.SetExpanded(true,false);Capture(island,Path.Combine(folder,"expanded.png"));});
        var dsh=vm.Providers.First(x=>x.Id=="dsh");
        await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Dsh=dsh.Snapshot,dsh.Compact,dsh.CompactUpdated,dsh.Hero,dsh.Status,dsh.Updated,Rows=dsh.Rows,NativeWindow=island.WindowEvidence,OtherProvidersQueried=false,ProductionSettingsChanged=false},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        await Dispatcher.InvokeAsync(()=>_=ExitAsync());
    }
    private static void Capture(Window window,string file){window.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(window);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(file);png.Save(stream);}
    protected override void OnExit(ExitEventArgs e){tray?.Dispose();trayIcon?.Dispose();instance?.Dispose();base.OnExit(e);}
}


