using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                        if(buffer[i]==10){var next=Parse(frame.ToArray(),Volatile.Read(ref current),DateTimeOffset.UtcNow);frame.SetLength(0);Volatile.Write(ref current,next);first.TrySetResult();}
                        else {if(frame.Length>=16384)throw new InvalidDataException("Bridge frame exceeded limit");frame.WriteByte(buffer[i]);}
                    }
                }
                MarkFailure("Balance Bridge 已断开；保留上次成功快照。");
            } catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException){MarkFailure("Balance Bridge 不可用或消息被拒绝；保留上次成功快照。");}
            try{await Task.Delay(1000,stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    private void MarkFailure(string message){var last=Volatile.Read(ref current);Volatile.Write(ref current,last with{State=last.Balance is null?DshBalanceState.Unavailable:DshBalanceState.Stale,Error=message});}
    public static DshBalanceReading Parse(ReadOnlySpan<byte> frame,DshBalanceReading previous,DateTimeOffset now)
    {
        if(frame.Length>16384)throw new InvalidDataException("Bridge frame exceeded limit");
        using var doc=JsonDocument.Parse(frame.ToArray());var e=doc.RootElement;
        if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty("protocol",out var protocol)||protocol.GetInt32()!=1||e.GetProperty("sourceVersion").GetString()!="0.2.0-rc.2")throw new InvalidDataException("Unsupported bridge");
        var allowed=new HashSet<string>{"protocol","sourceVersion","state","observedAt","normal_wallets","bonus_wallets","queryStatus"};
        if(e.EnumerateObject().Any(p=>!allowed.Contains(p.Name)))throw new InvalidDataException("Unexpected bridge field");
        if(e.TryGetProperty("queryStatus",out var query)&&query.GetString() is not("ready" or "loading" or "failed" or "signed-out" or "unavailable"))throw new InvalidDataException("Invalid query status");
        var state=e.GetProperty("state").GetString();
        if(state=="signed-out")return new(null,DshBalanceState.Unavailable,"DSH 官方客户端已退出账户；余额已清除。");
        if(state is "failed" or "loading" or "unavailable")return previous with{State=previous.Balance is null?DshBalanceState.Unavailable:DshBalanceState.Stale,Error="官方余额暂不可用；等待官方客户端成功更新。"};
        if(state is not("ready" or "stale")||!DateTimeOffset.TryParse(e.GetProperty("observedAt").GetString(),out var at)||at>now+TimeSpan.FromSeconds(5))throw new InvalidDataException("Invalid balance timestamp");
        if(previous.Balance is {} last&&at<last.ObservedAt)throw new InvalidDataException("Out-of-order snapshot");
        var balance=new DshBalance(ReadWallets(e.GetProperty("normal_wallets")),ReadWallets(e.GetProperty("bonus_wallets")),at,"0.2.0-rc.2");
        return new(balance,state=="stale"||now-at>DshBalanceReading.Freshness?DshBalanceState.Stale:DshBalanceState.Ready,state=="stale"?"DSH Host 余额查询暂不可用；保留最近有效余额。":null);
    }
    private static IReadOnlyList<WalletBalance> ReadWallets(JsonElement e)
    {
        if(e.ValueKind!=JsonValueKind.Array||e.GetArrayLength()>8)throw new InvalidDataException("Invalid wallets");
        var list=new List<WalletBalance>();
        foreach(var row in e.EnumerateArray()){
            if(row.ValueKind!=JsonValueKind.Object||row.EnumerateObject().Count()!=2||row.GetProperty("currency").ValueKind!=JsonValueKind.String||row.GetProperty("balance").ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid wallet");
            var currency=row.GetProperty("currency").GetString()!;var amount=row.GetProperty("balance").GetString()!;
            if(currency is not("CNY" or "USD")||!DshBalancePresentation.ValidAmount(amount))throw new InvalidDataException("Invalid wallet");
            list.Add(new(currency,amount));
        }
        return list;
    }
    public async ValueTask DisposeAsync(){await stop.CancelAsync();if(listener is not null)await listener;stop.Dispose();}
}
