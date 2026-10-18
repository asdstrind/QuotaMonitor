using System.Text.Json;
using System.Windows;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;
using QuotaMonitor.Services;
using System.Security.Cryptography;
using System.Text;

namespace QuotaMonitor;
public partial class App
{
    // Real Host/pipe/WPF check. No credentials, screenshots, balances or production store on disk.
    private async Task VerifyDshLive163Async()
    {
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","dsh-v1.6.3");Directory.CreateDirectory(folder);
        var events=new List<object>();
        var model=new MonitorViewModel();
        model.Providers.Add(new(ProviderSnapshot.Empty("dsh","DSH",MetricType.Balance)));
        var settings=new MonitorSettings{ShowCodex=false,ShowClaude=false,ShowAntigravity=false,HideInFullscreen=false};
        var today=DateOnly.FromDateTime(DateTime.Now);model.ApplyVisibility(settings,today);
        var window=new IslandWindow(model,settings,()=>{}){Title="QuotaMonitor v1.6.3 · Live verification"};MainWindow=window;
        window.VerificationSuspendsPointer=true;window.Show();window.SetState(IslandState.Expanded,false);
        NativeWindow.VerificationInspectable(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        await using var receiver=new DshBalanceReceiver();
        var duration=Option(Environment.GetCommandLineArgs(),"--seconds",900);
        var until=DateTimeOffset.UtcNow.AddSeconds(duration);string? last=null;
        try
        {
            while(DateTimeOffset.UtcNow<until)
            {
                var reading=await receiver.ReadAsync(default);var state=reading.At(DateTimeOffset.UtcNow);
                model.Dsh.Update(model.Dsh.Snapshot with{ConnectionStatus=ConnectionStatus.Ready,AccountBalance=reading});
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var key=$"{state}|{reading.Balance?.ObservedAt:O}";
                if(last!=key)
                {
                    var uiMatches=reading.Balance is null||FindVisual<System.Windows.Controls.TextBlock>((FrameworkElement)window.FindName("Instruments")).Any(t=>t.Text==DshBalancePresentation.Normal(reading.Balance));
                    bool? officialMatches=null;
                    var comparison=Path.Combine(Path.GetTempPath(),"QuotaMonitor-v163-official.sha256");
                    if(reading.Balance is {} balance&&File.Exists(comparison))
                    {
                        var text=string.Join("|",balance.NormalWallets.Concat(balance.BonusWallets).Select(DshBalancePresentation.Wallet));
                        officialMatches=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Equals(File.ReadAllText(comparison).Trim(),StringComparison.OrdinalIgnoreCase);
                    }
                    events.Add(new{At=DateTimeOffset.UtcNow,State=state.ToString(),ObservedAt=reading.Balance?.ObservedAt,HasBalance=reading.Balance is not null,UiMatchesReceiver=uiMatches,OfficialPageMatches=officialMatches,SourceVersion=reading.Balance?.SourceVersion});last=key;
                    await File.WriteAllTextAsync(Path.Combine(folder,"live-redacted.json"),JsonSerializer.Serialize(events,new JsonSerializerOptions{WriteIndented=true}));
                }
                await Task.Delay(500);
            }
        }
        finally{window.Close();Shutdown();}
    }
}
