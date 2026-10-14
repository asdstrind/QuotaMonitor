using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.Security;
using QuotaMonitor.Views;
namespace QuotaMonitor;
public partial class App
{
    private async Task VerifySettingsAsync(bool preview)
    {
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","interaction-v1.5.0","settings","gui");Directory.CreateDirectory(folder);
        var checks=new Dictionary<string,bool>();SettingsWindow? window=null;
        var target="QuotaMonitor/SettingsVerification/"+Environment.ProcessId;var secret=new CredentialStore(target);
        var client=new HttpClient(new SettingsTestHttp());
        var root=Path.Combine(folder,preview?"preview-store":"roundtrip-"+Environment.ProcessId);var isolated=new SettingsStore(root);
        var config=preview?isolated.Load():new MonitorSettings{ClaudeUserId="42",DshSessionRoot=Path.Combine(root,"not-installed-yet"),DshPricedModel="custom-retained",DshPriceBasis="legacy override",DshInputPerMillion=.25m,HeatmapScale="relative",ClaudeUsageSeconds=780,Left=763,Top=16};
        var source=StoreMigration.CandidateFolders(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","Local","QuotaMonitor")).FirstOrDefault(p=>File.Exists(Path.Combine(p,"snapshots.json")));
        string? Hash(string name)=>source is not null&&File.Exists(Path.Combine(source,name))?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,name)))):null;
        var before=StoreMigration.Files.ToDictionary(n=>n,Hash);
        var snapshots=source is null?[]:JsonSerializer.Deserialize<ProviderSnapshot[]>(File.ReadAllText(Path.Combine(source,"snapshots.json")))??[];
        var model=snapshots.FirstOrDefault(p=>p.ProviderId=="dsh")?.CurrentModel;
        int savedCount=0;
        SettingsWindow Open(MonitorSettings c){var w=new SettingsWindow(c,isolated,secret,client,()=>savedCount++,()=>model,false){Title="QuotaMonitor · Settings verification (isolated)"};MainWindow=w;w.Show();return w;}
        void Set(string name,string value)=>((TextBox)window!.FindName(name)).Text=value;
        void Click(string name)=>((Button)window!.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Nav(string name)=>((RadioButton)window!.FindName(name)).IsChecked=true;
        try
        {
            secret.Write("synthetic-old-token");window=Open(config);await Task.Delay(400);
            if(preview){window.Closed+=(_,_)=>{secret.Delete();client.Dispose();Shutdown();};return;}
            // Read v1.3.2-shaped settings and preserve every existing known property.
            var legacy=JsonSerializer.Serialize(config);using(var doc=JsonDocument.Parse(legacy))File.WriteAllText(Path.Combine(root,"settings.json"),JsonSerializer.Serialize(doc.RootElement.EnumerateObject().Where(p=>p.Name!="DshEstimateEnabled").ToDictionary(p=>p.Name,p=>p.Value)));
            var migrated=isolated.Load();checks["LegacySettingsRoundtrip"]=JsonSerializer.Serialize(config)==JsonSerializer.Serialize(migrated);
            var dpi=VisualTreeHelper.GetDpi(window).DpiScaleX;
            foreach(var page in new[]{"General","Providers","Alerts","Advanced"})
            {
                Nav(page+"Nav");if(page=="Providers")Nav("DshTab");await Task.Delay(200);DesktopCapture(window,Path.Combine(folder,page.ToLowerInvariant()+"-desktop.png"));
                foreach(var scale in new[]{1d,1.5,2}){var bitmap=AlphaRender(window,scale);WritePng(bitmap,Path.Combine(folder,$"{page.ToLowerInvariant()}-{scale*100:0}.png"));checks[$"Raster{page}{scale*100:0}"]=bitmap.PixelWidth==(int)Math.Ceiling(window.ActualWidth*scale);}
                var scroll=(ScrollViewer)window.FindName("PageScroll");checks[page+"NoHorizontalOverflow"]=scroll.ExtentWidth<=scroll.ViewportWidth+.5;
            }
            Nav("ProvidersNav");Nav("ClaudeTab");await Task.Delay(200);DesktopCapture(window,Path.Combine(folder,"claude-desktop.png"));
            ((PasswordBox)window.FindName("TokenBox")).Password="synthetic-cancelled-token";Click("RemoveToken");window.Close();
            checks["CancelDoesNotDeleteCredential"]=secret.Read()=="synthetic-old-token";checks["CancelDoesNotChangeSettings"]=isolated.Load().DshPricedModel=="custom-retained"&&savedCount==0;
            window=Open(config);await Task.Delay(150);Nav("AlertsNav");Set("FiveHourThreshold","NaN");checks["InvalidThresholdBlocksSave"]=!window.SaveForSmoke()&&window.IsVisible&&secret.Read()=="synthetic-old-token";Set("FiveHourThreshold","12.5");Set("WeeklyThreshold","9");Set("ClaudeThreshold","2.123456789");Set("DshThreshold","0");
            ((PasswordBox)window.FindName("TokenBox")).Password="synthetic-new-token";checks["PasswordMasked"]=((PasswordBox)window.FindName("TokenBox")).PasswordChar!='\0';
            checks["SaveWithMissingDshDirectory"]=window.SaveForSmoke();checks["NewCredentialSaved"]=secret.Read()=="synthetic-new-token";var persisted=isolated.Load();checks["ThresholdPrecisionAndDisabledAlert"]=persisted.CodexFiveHourLowPercent==12.5&&persisted.CodexWeeklyLowPercent==9&&persisted.ClaudeLowBalanceUsd==2.123456789m&&persisted.DshLowBalance==0;
            checks["UneditedLegacyFieldsRetained"]=persisted.HeatmapScale=="relative"&&persisted.ClaudeUsageSeconds==780&&persisted.Left==763&&persisted.DshPricedModel=="custom-retained"&&persisted.DshInputPerMillion==.25m;
            checks["NoTokenInSettingsOrBackup"]=!File.ReadAllText(Path.Combine(root,"settings.json")).Contains("synthetic")&&!File.ReadAllText(Path.Combine(root,"settings.json.bak")).Contains("synthetic");
            window=Open(persisted);await Task.Delay(150);checks["RestartLoadsSavedThreshold"]=((TextBox)window.FindName("FiveHourThreshold")).Text=="12.5";checks["BlankPasswordKeepsCredential"]=window.SaveForSmoke()&&secret.Read()=="synthetic-new-token";
            window=Open(isolated.Load());await Task.Delay(150);Nav("AdvancedNav");((Expander)window.FindName("PriceExpander")).IsExpanded=true;Set("PriceModel","custom-new");Set("PriceInput","0.333");((CheckBox)window.FindName("EstimateOn")).IsChecked=false;Set("DshPathBox","");await Task.Delay(150);DesktopCapture(window,Path.Combine(folder,"advanced-override-desktop.png"));checks["AdvancedOverrideAndAutoPathSave"]=window.SaveForSmoke();persisted=isolated.Load();checks["AdvancedPersists"]=persisted.DshPricedModel=="custom-new"&&persisted.DshInputPerMillion==.333m&&!persisted.DshEstimateEnabled&&persisted.DshSessionRoot=="";
            window=Open(persisted);await Task.Delay(150);Nav("ProvidersNav");Nav("ClaudeTab");Set("CodexInterval","bad-unrelated-field");Click("TestButton");await Task.Delay(250);checks["ConnectionTestIndependentOfOtherSettings"]=((TextBlock)window.FindName("TestResult")).Text.StartsWith("连接成功");window.Close();
            window=Open(persisted);await Task.Delay(150);Click("RemoveToken");checks["RemoveDeferredUntilSave"]=secret.Read()=="synthetic-new-token";checks["ExplicitRemoveCommits"]=window.SaveForSmoke()&&secret.Read() is null;
            // Failed persistence must roll back a replacement credential.
            secret.Write("synthetic-before-failure");var badStore=new SettingsStore(Path.Combine(root,"failed-save"));Directory.CreateDirectory(Path.Combine(badStore.Root,"settings.json"));window=new SettingsWindow(persisted,badStore,secret,client,()=>{},null,false);window.Show();await Task.Delay(150);((PasswordBox)window.FindName("TokenBox")).Password="synthetic-failed-replacement";checks["FailedSaveRestoresCredential"]=!window.SaveForSmoke()&&secret.Read()=="synthetic-before-failure";window.Close();
            window=Open(persisted);await Task.Delay(150);window.Width=600;window.Height=360;
            foreach(var page in new[]{"General","Providers","Alerts","Advanced"}){Nav(page+"Nav");await Task.Delay(150);var scroll=(ScrollViewer)window.FindName("PageScroll");checks["Compact"+page+"NoHorizontalOverflow"]=scroll.ExtentWidth<=scroll.ViewportWidth+.5;var nav=(RadioButton)window.FindName(page+"Nav");checks["Compact"+page+"NavigationReachable"]=nav.TranslatePoint(new Point(0,nav.ActualHeight),window).Y<((Button)window.FindName("SaveButton")).TranslatePoint(new Point(),window).Y;checks["Compact"+page+"SaveReachable"]=((Button)window.FindName("SaveButton")).IsVisible;DesktopCapture(window,Path.Combine(folder,"compact-"+page.ToLowerInvariant()+".png"));}
            checks["ProductionStoreUntouched"]=before.All(p=>p.Value==Hash(p.Key));
            await File.WriteAllTextAsync(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,ActualNativeDpi=dpi,RasterScales=new[]{1d,1.5,2},ModelSource=model is null?"waiting for Session":"real cached Session model",Before=before,After=StoreMigration.Files.ToDictionary(n=>n,Hash),Credentials="synthetic, unique per-process credential target",Http="isolated test handler, no production Token transmitted",Startup="no registry writes"},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(folder,"failure.txt"),ex.ToString());checks["ExceptionFree"]=false;}
        finally{if(!preview){window?.Close();try{secret.Delete();}finally{client.Dispose();Shutdown(checks.Count>0&&checks.Values.All(v=>v)?0:1);}}}
    }
    private sealed class SettingsTestHttp:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var data=request.RequestUri!.AbsolutePath switch{"/api/user/self"=>"{\"quota\":500000,\"used_quota\":1}","/api/log/self/stat"=>"{\"quota\":1,\"total_requests\":1,\"total_tokens\":100}",_=>"{\"items\":[]}"};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"success\":true,\"data\":"+data+"}")});
        }
    }
}



