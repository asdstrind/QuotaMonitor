using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Tests;
public class DshBalanceTests
{
    [Theory]
    [InlineData("0.2.0-rc.2")]
    [InlineData("0.2.1-rc.1")]
    [InlineData("0.2.1-rc.2")]
    public void TestedHostVersionsPreserveWalletsAndSource(string version)
    {
        var frame=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Frame()).Replace("0.2.0-rc.2",version));
        var reading=DshBalanceReceiver.Parse(frame,DshBalanceReading.Empty,Now);
        Assert.Equal(DshBalanceState.Ready,reading.State);Assert.Equal(version,reading.Balance!.SourceVersion);
        Assert.Equal("123.123456789012345678901234567890123456",reading.Balance.NormalWallets[0].Balance);
    }
    [Fact] public void UntestedHostVersionIsRejected()
    {
        var frame=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Frame()).Replace("0.2.0-rc.2","0.3.0"));
        Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(frame,DshBalanceReading.Empty,Now));
    }
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-10T10:00:00Z");
    private static byte[] Frame(string state="ready",DateTimeOffset? at=null)=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=1,sourceVersion="0.2.0-rc.2",state,observedAt=(at??Now).ToString("O"),normal_wallets=new[]{new{currency="CNY",balance="123.123456789012345678901234567890123456"},new{currency="USD",balance="1.0000000000"}},bonus_wallets=new[]{new{currency="CNY",balance="0.0000000001"}}});
    [Fact] public void StoreImportCopiesOnlyThisAppsFilesFromItsOwnFoldersOnce()
    {
        var root=Path.Combine(Path.GetTempPath(),"quota-import-"+Guid.NewGuid());var local=Path.Combine(root,"local");
        var packaged=Path.Combine(local,"Packages","Some.Package_abc","LocalCache","Local","QuotaMonitor");
        var plain=Path.Combine(local,"QuotaMonitor");var destination=Path.Combine(root,"target");
        Directory.CreateDirectory(packaged);Directory.CreateDirectory(plain);Directory.CreateDirectory(destination);
        try
        {
            File.WriteAllText(Path.Combine(packaged,"settings.json"),"{\"Probe\":\"packaged\"}");
            File.WriteAllText(Path.Combine(packaged,"snapshots.json"),"{\"Probe\":\"packaged\"}");
            File.WriteAllText(Path.Combine(packaged,"credentials.json"),"must not be copied");
            File.WriteAllText(Path.Combine(plain,"history.json"),"{\"Probe\":\"plain\"}");
            File.WriteAllText(Path.Combine(plain,"settings.json"),"{\"Probe\":\"plain\"}");
            Assert.Equal(3,StoreMigration.Import(local,destination,3));
            Assert.Equal("{\"Probe\":\"packaged\"}",File.ReadAllText(Path.Combine(destination,"settings.json")));
            Assert.True(File.Exists(Path.Combine(destination,"snapshots.json")));
            Assert.True(File.Exists(Path.Combine(destination,"history.json")));
            Assert.False(File.Exists(Path.Combine(destination,"credentials.json")));
            // Nothing is overwritten, so a second import has nothing left to copy.
            Assert.Equal(0,StoreMigration.Import(local,destination,3));
            Assert.Equal("{\"Probe\":\"plain\"}",File.ReadAllText(Path.Combine(plain,"settings.json")));
            // The budget caps how much one launch copies.
            var limited=Path.Combine(root,"limited");Directory.CreateDirectory(limited);
            Assert.Equal(2,StoreMigration.Import(local,limited,2));
            Assert.Equal(2,Directory.GetFiles(limited).Length);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public void BothWalletsPreserveCurrencyAndArbitraryPrecision(){var r=DshBalanceReceiver.Parse(Frame(),DshBalanceReading.Empty,Now);Assert.Equal(DshBalanceState.Ready,r.State);Assert.Equal("123.123456789012345678901234567890123456",r.Balance!.NormalWallets[0].Balance);Assert.Equal("USD",r.Balance.NormalWallets[1].Currency);Assert.Equal("0.0000000001",r.Balance.BonusWallets[0].Balance);Assert.Equal(Now,r.Balance.ObservedAt);}
    [Fact] public void HostFailureReplayPreservesWalletsAsStaleAfterMonitorRestart(){var json=Encoding.UTF8.GetString(Frame()).Replace("\"state\":\"ready\"","\"state\":\"stale\",\"queryStatus\":\"failed\"");var reading=DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(json),DshBalanceReading.Empty,Now);Assert.Equal(DshBalanceState.Stale,reading.State);Assert.Equal("123.123456789012345678901234567890123456",reading.Balance!.NormalWallets[0].Balance);Assert.Equal(Now,reading.Balance.ObservedAt);Assert.NotNull(reading.Error);}
    [Fact] public void UnknownQueryStatusIsRejected(){var json=Encoding.UTF8.GetString(Frame()).Replace("\"protocol\":1","\"protocol\":1,\"queryStatus\":\"unexpected\"");Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(json),DshBalanceReading.Empty,Now));}
    [Fact] public void FailedAndLoadingPreserveLastSuccessWithoutRenewingTime(){var last=DshBalanceReceiver.Parse(Frame(),DshBalanceReading.Empty,Now);foreach(var state in new[]{"failed","loading","unavailable"}){var next=DshBalanceReceiver.Parse(Frame(state,Now.AddMinutes(1)),last,Now.AddMinutes(1));Assert.Same(last.Balance,next.Balance);Assert.Equal(DshBalanceState.Stale,next.State);Assert.Equal(Now,next.Balance!.ObservedAt);}}
    [Fact] public void OldSnapshotsRemainStaleAndSignOutClearsAccount(){var last=DshBalanceReceiver.Parse(Frame(at:Now.AddMinutes(-5)),DshBalanceReading.Empty,Now);Assert.Equal(DshBalanceState.Stale,last.State);Assert.Null(DshBalanceReceiver.Parse(Frame("signed-out"),last,Now).Balance);Assert.Equal(DshBalanceState.Stale,DshBalanceReceiver.Parse(Frame(),DshBalanceReading.Empty,Now).At(Now.AddMinutes(5)));}
    [Fact] public void FutureOutOfOrderAndUnsupportedMessagesAreRejected(){Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Frame(at:Now.AddHours(1)),DshBalanceReading.Empty,Now));var last=DshBalanceReceiver.Parse(Frame(),DshBalanceReading.Empty,Now);Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Frame(at:Now.AddSeconds(-1)),last,Now));Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Frame()).Replace("\"protocol\":1","\"protocol\":2")),last,Now));}
    [Theory]
    [InlineData("{\"currency\":\"CNY\",\"balance\":1}")]
    [InlineData("{\"currency\":\"EUR\",\"balance\":\"1\"}")]
    [InlineData("{\"currency\":\"USD\",\"balance\":\"NaN\"}")]
    [InlineData("{\"currency\":\"USD\",\"balance\":\"1\",\"secret\":\"synthetic\"}")]
    public void InvalidWalletsDoNotBecomeZero(string wallet){var json=$$"""{"protocol":1,"sourceVersion":"0.2.0-rc.2","state":"ready","observedAt":"2026-10-10T10:00:00Z","normal_wallets":[{{wallet}}],"bonus_wallets":[]}""";Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(json),DshBalanceReading.Empty,Now));}
    [Fact] public void UnexpectedFieldsAndOversizedFramesAreRejected(){var extra=Encoding.UTF8.GetString(Frame()).Replace("\"protocol\":1","\"secret\":\"synthetic\",\"protocol\":1");Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(extra),DshBalanceReading.Empty,Now));Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(new byte[16385],DshBalanceReading.Empty,Now));}
    [Theory]
    [InlineData("0","$0.00")]
    [InlineData("-0.0000","$0.00")]
    [InlineData("0.000000001","<$0.01")]
    [InlineData("1.99999","$1.99")]
    [InlineData("-0.000001","-$0.01")]
    [InlineData("-1.99999","-$2.00")]
    [InlineData("123456789012345678901234567890.123456789","$123,456,789,012,345,678,901,234,567,890.12")]
    [InlineData(".1e-2","<$0.01")]
    public void CompactMatchesOfficialFormattingWithoutFloatingPoint(string value,string expected)=>Assert.Equal(expected,DshBalancePresentation.Wallet(new("USD",value)));
    [Fact] public async Task MissingSessionRootDoesNotHideValidBalance(){var adapter=new DshAdapter(new MonitorSettings{DshSessionRoot=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString())},new FixedSource(DshBalanceReceiver.Parse(Frame(at:DateTimeOffset.UtcNow),DshBalanceReading.Empty,DateTimeOffset.UtcNow)));var result=await adapter.RefreshAsync(true,CancellationToken.None);Assert.Equal(ConnectionStatus.Partial,result.ConnectionStatus);Assert.NotNull(result.AccountBalance!.Balance);Assert.Null(result.TodayTokens);}
    [Fact] public void BothUiModesUseWalletBalanceAndItsOwnTimestamp(){var at=DateTimeOffset.UtcNow;var reading=DshBalanceReceiver.Parse(Frame(at:at),DshBalanceReading.Empty,at);var vm=new QuotaMonitor.ViewModels.ProviderViewModel(ProviderSnapshot.Empty("dsh","DSH",MetricType.Balance) with{ConnectionStatus=ConnectionStatus.Partial,AccountBalance=reading,TodayTokens=999,LastUpdated=at.AddHours(-1)});Assert.Equal("¥123.12 + $1.00",vm.Compact);Assert.Equal(vm.Compact,vm.Hero);Assert.Contains("BALANCE LIVE",vm.Status);Assert.Equal(at.LocalDateTime.ToString("HH:mm:ss"),vm.CompactUpdated);Assert.Contains("123.123456789012345678901234567890123456",vm.Rows.First().Value);Assert.Contains("赠送钱包",vm.Rows.ElementAt(1).Label);Assert.Contains("0.0000000001",vm.Rows.ElementAt(1).Value);vm.Update(vm.Snapshot with{AccountBalance=reading with{State=DshBalanceState.Stale}});Assert.Equal("Stale",vm.Compact);Assert.Contains("STALE",vm.Status);Assert.Equal("¥123.12 + $1.00",vm.Hero);}
    [Fact] public async Task RealPipeHandlesFragmentationInvalidMessagesReconnectAndCancellation(){var name="QuotaMonitor.Test."+Guid.NewGuid();await using var receiver=new DshBalanceReceiver(name);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));var first=receiver.ReadAsync(ct.Token);using(var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous)){await client.ConnectAsync(ct.Token);var bytes=Frame(at:DateTimeOffset.UtcNow);await client.WriteAsync(bytes.AsMemory(0,17),ct.Token);await client.WriteAsync(bytes.AsMemory(17),ct.Token);await client.WriteAsync(new byte[]{10},ct.Token);Assert.NotNull((await first).Balance);await client.WriteAsync(Encoding.UTF8.GetBytes("{\"protocol\":1}\n"),ct.Token);}await Task.Delay(1300,ct.Token);using(var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous)){await client.ConnectAsync(ct.Token);var bytes=Frame(at:DateTimeOffset.UtcNow);await client.WriteAsync(bytes,ct.Token);await client.WriteAsync(new byte[]{10},ct.Token);for(int i=0;i<20&&(await receiver.ReadAsync(ct.Token)).State!=DshBalanceState.Ready;i++)await Task.Delay(50,ct.Token);Assert.Equal(DshBalanceState.Ready,(await receiver.ReadAsync(ct.Token)).State);} }
    private sealed class FixedSource(DshBalanceReading value):IDshBalanceSource{public Task<DshBalanceReading> ReadAsync(CancellationToken ct)=>Task.FromResult(value);}
    [Fact] public async Task PersistedCacheIsStaleUntilAnOfficialSnapshotArrives(){var old=DshBalanceReceiver.Parse(Frame(),DshBalanceReading.Empty,Now).Balance;await using var receiver=new DshBalanceReceiver("QuotaMonitor.Test."+Guid.NewGuid(),old);var r=await receiver.ReadAsync(CancellationToken.None);Assert.Same(old,r.Balance);Assert.Equal(DshBalanceState.Stale,r.State);Assert.Equal(Now,r.Balance!.ObservedAt);}
}
