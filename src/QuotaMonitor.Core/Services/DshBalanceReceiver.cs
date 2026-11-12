using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using QuotaMonitor.Core.Models;

namespace QuotaMonitor.Core.Services;

public interface IDshBalanceSource
{
    Task<DshBalanceReading> ReadAsync(CancellationToken ct);
}

public sealed class DshBalanceReceiver : IDshBalanceSource, IAsyncDisposable
{
    public static string PipeName => "QuotaMonitor.DshBalance."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToLowerInvariant()))).ToLowerInvariant()[..16];
    private readonly string name;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? listener;
    private DshBalanceReading current = DshBalanceReading.Empty;
    public DshBalanceReceiver(string? pipeName=null,DshBalance? cachedBalance=null)
    {
        name=pipeName??PipeName;
        // A persisted snapshot can be shown after restart only as stale, until DSH confirms it.
        if(cachedBalance is not null)current=new(cachedBalance,DshBalanceState.Stale,"上次余额缓存；等待官方客户端快照。");
    }
    public async Task<DshBalanceReading> ReadAsync(CancellationToken ct)
    {
        lock(stop){listener??=RunAsync();}
        if(!first.Task.IsCompleted)try{await first.Task.WaitAsync(TimeSpan.FromSeconds(2),ct);}catch(TimeoutException){}
        return Volatile.Read(ref current);
    }
    private async Task RunAsync()
    {
        while(!stop.IsCancellationRequested) {
            try {
                // DSH is the pipe client; the monitor owns a current-user-only server and its ACL.
                using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance,4096,4096);
                await pipe.WaitForConnectionAsync(stop.Token);
                var buffer=new byte[4096];using var frame=new MemoryStream();
                while(pipe.IsConnected&&!stop.IsCancellationRequested) {
                    using var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    if(frame.Length>0)readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    int read;
                    try{read=await pipe.ReadAsync(buffer,readTimeout.Token);}catch(OperationCanceledException)when(!stop.IsCancellationRequested){throw new InvalidDataException("Incomplete bridge frame timeout");}
                    if(read==0)break;
                    for(int i=0;i<read;i++) {
                        if(buffer[i]==10){DshBalanceReading next;try{next=Parse(frame.ToArray(),Volatile.Read(ref current),DateTimeOffset.UtcNow);}catch(InvalidDataException){DshBridgeDiagnostics.Write("receiver-protocol-rejected");throw;}frame.SetLength(0);Volatile.Write(ref current,next);first.TrySetResult();}
                        else {if(frame.Length>=16384)throw new InvalidDataException("Bridge frame exceeded limit");frame.WriteByte(buffer[i]);}
                    }
                }
                DshBridgeDiagnostics.Write("ipc-disconnected");MarkFailure("Balance Bridge 已断开；保留上次成功快照。");
            } catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException){DshBridgeDiagnostics.Write(ex is UnauthorizedAccessException?"ipc-access-denied":ex is InvalidDataException?"receiver-frame-rejected":"ipc-unavailable");MarkFailure("Balance Bridge 不可用或消息被拒绝；保留上次成功快照。");}
            try{await Task.Delay(1000,stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    private void MarkFailure(string message){var last=Volatile.Read(ref current);Volatile.Write(ref current,last with{State=last.Balance is null?DshBalanceState.Unavailable:DshBalanceState.Stale,Error=message});}
    public static DshBalanceReading Parse(ReadOnlySpan<byte> frame,DshBalanceReading previous,DateTimeOffset now)
    {
        if(frame.Length>16384)throw new InvalidDataException("Bridge frame exceeded limit");
        try {
            using var doc=JsonDocument.Parse(frame.ToArray(),new JsonDocumentOptions{MaxDepth=8});var e=doc.RootElement;
            Fields(e,["protocol","sourceVersion","state","observedAt","normal_wallets","bonus_wallets","queryStatus"]);
            if(!e.GetProperty("protocol").TryGetInt32(out var protocol)||protocol!=1)throw new InvalidDataException("Unsupported bridge protocol");
            // Host version is bounded diagnostic metadata. Protocol alone owns the wire contract.
            var version=e.GetProperty("sourceVersion").GetString();
            if(version is null||version.Length is <1 or >128||!Regex.IsMatch(version,@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z",RegexOptions.CultureInvariant))throw new InvalidDataException("Invalid Host version metadata");
            var state=e.GetProperty("state").GetString();
            if(state is not("ready" or "stale" or "signed-out" or "failed" or "loading" or "unavailable"))throw new InvalidDataException("Invalid state");
            var status=e.TryGetProperty("queryStatus",out var query)?query.GetString():null;
            if(query.ValueKind!=JsonValueKind.Undefined&&(status is not("ready" or "loading" or "failed" or "signed-out" or "unavailable")||state=="stale"&&status is not("loading" or "failed" or "unavailable")||state!="stale"&&status!=state))throw new InvalidDataException("Inconsistent query status");
            var hasAt=e.TryGetProperty("observedAt",out var time);
            var at=DateTimeOffset.MinValue;
            if(hasAt) {
                var stamp=time.GetString();
                if(stamp is null||stamp.Length>40||!Regex.IsMatch(stamp,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.[0-9]{1,7})?(?:Z|[+-]\d{2}:\d{2})\z",RegexOptions.CultureInvariant)||!DateTimeOffset.TryParse(stamp,CultureInfo.InvariantCulture,DateTimeStyles.None,out at)||at<DateTimeOffset.UnixEpoch||at>now+TimeSpan.FromSeconds(5))throw new InvalidDataException("Invalid balance timestamp");
            }
            var hasNormal=e.TryGetProperty("normal_wallets",out var normal);var hasBonus=e.TryGetProperty("bonus_wallets",out var bonus);
            IReadOnlyList<WalletBalance>? normalWallets=null,bonusWallets=null;
            if(hasNormal||hasBonus) { if(!hasNormal||!hasBonus||!hasAt)throw new InvalidDataException("Incomplete wallets");normalWallets=ReadWallets(normal);bonusWallets=ReadWallets(bonus); }
            // Validate even ignored fields before allowing a state transition.
            if(state=="signed-out")return new(null,DshBalanceState.Unavailable,"DSH 官方客户端已退出账户；余额已清除。");
            if(state is "failed" or "loading" or "unavailable")return previous with{State=previous.Balance is null?DshBalanceState.Unavailable:DshBalanceState.Stale,Error="官方余额暂不可用；等待官方客户端成功更新。"};
            if(!hasAt||normalWallets is null||bonusWallets is null)throw new InvalidDataException("Incomplete balance snapshot");
            if(previous.Balance is {} last&&at<last.ObservedAt)throw new InvalidDataException("Out-of-order snapshot");
            var balance=new DshBalance(normalWallets,bonusWallets,at,version);
            return new(balance,state=="stale"||now-at>DshBalanceReading.Freshness?DshBalanceState.Stale:DshBalanceState.Ready,state=="stale"?"DSH Host 余额查询暂不可用；保留最近有效余额。":null);
        } catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { throw new InvalidDataException("Invalid bridge message"); }
    }
    private static void Fields(JsonElement e,string[] allowed)
    {
        if(e.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Expected object");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in e.EnumerateObject())if(!allowed.Contains(p.Name)||!seen.Add(p.Name))throw new InvalidDataException("Unexpected or duplicate field");
    }
    private static IReadOnlyList<WalletBalance> ReadWallets(JsonElement e)
    {
        if(e.ValueKind!=JsonValueKind.Array||e.GetArrayLength()>8)throw new InvalidDataException("Invalid wallets");
        var list=new List<WalletBalance>();
        foreach(var row in e.EnumerateArray()){
            Fields(row,["currency","balance"]);
            if(row.EnumerateObject().Count()!=2||row.GetProperty("currency").ValueKind!=JsonValueKind.String||row.GetProperty("balance").ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid wallet");
            var currency=row.GetProperty("currency").GetString()!;var amount=row.GetProperty("balance").GetString()!;
            if(currency is not("CNY" or "USD")||!DshBalancePresentation.ValidAmount(amount))throw new InvalidDataException("Invalid wallet");
            list.Add(new(currency,amount));
        }
        return list;
    }
    public async ValueTask DisposeAsync(){await stop.CancelAsync();if(listener is not null)await listener;stop.Dispose();}
}
