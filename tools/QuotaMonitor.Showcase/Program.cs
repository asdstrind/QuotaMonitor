using System.Reflection;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Security;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

// Offline showcase only. Never instantiate QuotaMonitor.App or any adapter.
// All values are invented; Settings uses a random, nonexistent credential target.
public static class Showcase
{
    [STAThread]
    public static void Main(string[] args)
    {
        if(args.Length!=2)throw new ArgumentException("Arguments: repository root, output directory");
        var root=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var x=XDocument.Load(Path.Combine(root,"src/QuotaMonitor/App.xaml"));
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary=new XElement(ns+"ResourceDictionary",x.Root!.Element(ns+"Application.Resources")!.Elements());
        dictionary.SetAttributeValue(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        app.Startup+=async(_,_)=>{
            try{await Generate(root,output);app.Shutdown(0);}catch(Exception e){File.WriteAllText(Path.Combine(output,"error.txt"),e.ToString());app.Shutdown(1);}
        };
        app.Run();
    }
    static async Task Generate(string root,string output)
    {
        var vm=new MonitorViewModel();var now=DateTimeOffset.UtcNow;var today=DateOnly.FromDateTime(DateTime.Now);
        var settings=new MonitorSettings{TopCenter=false,Left=50,Top=50,HideInFullscreen=false,AutoStart=false,ClaudeUserId="",CodexExecutable="codex",DshSessionRoot=""};
        foreach(var id in UsageGrid.Order){
            vm.Providers.Add(new(new ProviderSnapshot{ProviderId=id,DisplayName=MonitorViewModel.NameOf(id),ConnectionStatus=ConnectionStatus.Ready,
                LastUpdated=now,UsageUpdated=now,RemainingPercent=72,RemainingBalance=18.50m,Currency="USD",TodayTokens=284000,TotalTokens=8600000,TodayRequests=42,TodayCost=.42m,TotalCost=12.80m,
                InputTokens=240000,OutputTokens=44000,CurrentModel="synthetic-model",DataSource="Synthetic showcase",
                Windows=id=="codex"?[new("codex","Primary",28,300,now.AddHours(2)),new("codex","Weekly",38,10080,now.AddDays(3))]:id=="antigravity"?[new("gemini-weekly","Gemini",32,10080,now.AddDays(4)),new("3p-weekly","Claude/GPT",54,10080,now.AddDays(5))]:[],
                AccountBalance=id=="dsh"?new(new([new("CNY","42.80")],[new("CNY","5.00")],now,"synthetic showcase"),DshBalanceState.Ready,null):null}));
            var lane=UsageGrid.Order.ToList().IndexOf(id);
            vm.UpdateHistory(new(){ProviderId=id,Source="Synthetic showcase",Scope="Invented daily aggregates",CostCurrency=id=="dsh"?"CNY":"USD",CoverageStart=today.AddDays(-180),
                Days=Enumerable.Range(0,181).Select(i=>new UsageDay(today.AddDays(i-180),i%11==0?0:(long)(800*Math.Pow(1.034,i%120)*(1+(i*7+lane*13)%17)),(i*3+lane)%50,.03m*(i%9),UsageQuality.Exact,now)).ToArray()},today);
        }
        vm.Rebuild(today);vm.ApplyVisibility(settings,today);vm.SettingsCommand=vm.RefreshCommand=vm.ExitCommand=new ActionCommand(()=>{});
        var window=new IslandWindow(vm,settings,()=>{});
        typeof(IslandWindow).GetProperty("VerificationSuspendsPointer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);
        window.Show();await Task.Delay(350);
        window.SetState(IslandState.Expanded,false);await Task.Delay(160);Capture(window,Path.Combine(output,"expanded.png"));
        window.SetState(IslandState.Collapsed,false);await Task.Delay(100);Capture(window,Path.Combine(output,"collapsed.png"));
        window.SetState(IslandState.Minimal,false);await Task.Delay(100);Capture(window,Path.Combine(output,"minimal.png"));
        // Capture actual production morph on a fixed canvas, with a fixed synthetic model.
        var frames=Path.Combine(output,"frames");Directory.CreateDirectory(frames);
        for(var i=0;i<150;i++){
            if(i==25)window.SetState(IslandState.Collapsed);
            if(i==55)window.SetState(IslandState.Expanded);
            if(i==115)window.SetState(IslandState.Minimal);
            await Task.Delay(40);Capture(window,Path.Combine(frames,$"{i:D4}.png"));
        }
        vm.Codex.Update(vm.Codex.Snapshot with{RemainingPercent=12,Windows=[new("codex","Primary",88,300,now.AddHours(2)),new("codex","Weekly",92,10080,now.AddDays(3))]});
        vm.Claude.Update(vm.Claude.Snapshot with{RemainingBalance=.52m});
        vm.Dsh.Update(vm.Dsh.Snapshot with{AccountBalance=new(new([new("CNY","0.80")],[],now,"synthetic showcase"),DshBalanceState.Ready,null)});
        vm.Antigravity.Update(vm.Antigravity.Snapshot with{Windows=[new("gemini-weekly","Gemini",89,10080,now.AddDays(4)),new("3p-weekly","Claude/GPT",93,10080,now.AddDays(5))]});
        vm.UpdateAlerts(settings,now);window.SetState(IslandState.Minimal,false);await Task.Delay(180);Capture(window,Path.Combine(output,"alerts.png"));window.Close();
        using var http=new HttpClient(new OfflineHandler());
        var store=new SettingsStore(Path.Combine(output,"empty-store"));
        var dialog=new SettingsWindow(settings,store,new CredentialStore("QuotaMonitor/Showcase/"+Guid.NewGuid()),http,()=>{},startupAllowed:false);
        dialog.Show();await Task.Delay(200);Capture(dialog,Path.Combine(output,"settings.png"));dialog.Close();
        File.WriteAllText(Path.Combine(output,"provenance.txt"),"Rendered from unchanged production WPF views. All balances, quota, history and account fields are synthetic. No adapters, account files, production store or network. Settings: empty isolated store, random nonexistent credential target, startup disabled. No screen capture.");
    }
    static void Capture(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*1.5),(int)Math.Ceiling(window.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
        bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    sealed class OfflineHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>throw new InvalidOperationException("Network disabled in showcase");
    }
}
