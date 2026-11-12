using System.IO.Pipes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

namespace QuotaMonitor;
public partial class App
{
    // Local acceptance harness: no production store, screenshots, wallet values or account IDs on disk.
    private async Task VerifyDshLive165Async()
    {
        var folder=Path.Combine(Environment.CurrentDirectory,"evidence","v1.6.5");Directory.CreateDirectory(folder);
        var events=new List<object>();var comparisons=new List<object>();
        using var comparisonKey=RSA.Create(2048);
        await File.WriteAllTextAsync(Path.Combine(folder,"verification-public-key.pem"),comparisonKey.ExportSubjectPublicKeyInfoPem());
        var model=new MonitorViewModel();model.Providers.Add(new(ProviderSnapshot.Empty("dsh","DSH",MetricType.Balance)));
        var config=new MonitorSettings{ShowCodex=false,ShowGo=false,ShowAntigravity=false,HideInFullscreen=false};
        model.ApplyVisibility(config,DateOnly.FromDateTime(DateTime.Now));
        var window=new IslandWindow(model,config,()=>{}){Title="QuotaMonitor v1.6.5 · Live verification"};MainWindow=window;
        window.VerificationSuspendsPointer=true;window.Show();window.SetState(IslandState.Expanded,false);
        await using var receiver=new DshBalanceReceiver();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(Option(Environment.GetCommandLineArgs(),"--seconds",1200)));
        DshBalanceReading latest=DshBalanceReading.Empty;
        var compareTask=CompareAsync();
        async Task CompareAsync()
        {
            while(!stop.IsCancellationRequested)try {
                // Test input is read transiently; the only persisted outcome is equality.
                using var pipe=new NamedPipeServerStream("QuotaMonitor.Verify165",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader=new StreamReader(pipe,leaveOpen:true);using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
                var bytes=new List<byte>();var one=new byte[1];
                while(bytes.Count<=4096&&await pipe.ReadAsync(one,timeout.Token)>0&&one[0]!=10)bytes.Add(one[0]);
                if(bytes.Count>4096)throw new InvalidDataException();
                // The UI automation sandbox cannot open a current-user pipe. A public-key
                // envelope lets a normal-user relay forward opaque bytes without logging money.
                using var envelope=JsonDocument.Parse(bytes.ToArray());
                using var document=JsonDocument.Parse(comparisonKey.Decrypt(Convert.FromBase64String(envelope.RootElement.GetProperty("encrypted").GetString()!),RSAEncryptionPadding.OaepSHA256));
                var input=document.RootElement;
                var balance=Volatile.Read(ref latest).Balance;
                var expectedNormal=balance?.NormalWallets.Select(DshBalancePresentation.Wallet).ToArray();
                // The official page hides non-positive promotional wallets. Match its display,
                // while the receiver continues retaining every original wallet string.
                var expectedBonus=balance?.BonusWallets.Select(DshBalancePresentation.Wallet).Where(x=>!x.StartsWith('-')&&x is not("¥0.00" or "$0.00")).ToArray();
                var normalMatches=balance is not null&&input.GetProperty("normal").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(expectedNormal!);
                var bonusMatches=balance is not null&&input.GetProperty("bonus").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(expectedBonus!);
                var matches=normalMatches&&bonusMatches;
                comparisons.Add(new{At=DateTimeOffset.UtcNow,OfficialPageMatches=matches,NormalMatches=normalMatches,VisibleBonusMatches=bonusMatches});
                await File.WriteAllTextAsync(Path.Combine(folder,"official-comparison-redacted.json"),JsonSerializer.Serialize(comparisons),stop.Token);
                await writer.WriteLineAsync(matches?"true":"false");
            }catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex)when(ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or CryptographicException or FormatException){}
        }
        try {
            string? last=null;
            while(!stop.IsCancellationRequested) {
                var reading=await receiver.ReadAsync(stop.Token);Volatile.Write(ref latest,reading);
                var state=reading.At(DateTimeOffset.UtcNow);
                model.Dsh.Update(model.Dsh.Snapshot with{ConnectionStatus=ConnectionStatus.Ready,AccountBalance=reading});
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var key=$"{state}|{reading.Balance?.ObservedAt:O}";
                if(last!=key) {
                    var uiMatches=reading.Balance is null||FindVisual<System.Windows.Controls.TextBlock>((FrameworkElement)window.FindName("Instruments")).Any(t=>t.Text==DshBalancePresentation.Normal(reading.Balance));
                    events.Add(new{At=DateTimeOffset.UtcNow,State=state.ToString(),ObservedAt=reading.Balance?.ObservedAt,HasBalance=reading.Balance is not null,UiMatchesReceiver=uiMatches,SourceVersion=reading.Balance?.SourceVersion});
                    await File.WriteAllTextAsync(Path.Combine(folder,"live-redacted.json"),JsonSerializer.Serialize(events,new JsonSerializerOptions{WriteIndented=true}),stop.Token);last=key;
                }
                await Task.Delay(500,stop.Token);
            }
        }catch(OperationCanceledException){}
        finally {await stop.CancelAsync();await compareTask;window.Close();Shutdown();}
    }
}
