using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Core.Adapters;

public sealed class OpenCodeGoAdapter(HttpClient http, MonitorSettings settings, ISecretStore secrets, TimeProvider? clock = null) : IProviderAdapter, IUsageHistorySource
{
    public const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";
    public const string ExportUrl = "https://opencode.ai/console/api/v2/usage/export?range=90d";
    public string Id => "opencode-go";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1);
    private string? credential;
    private ProviderSnapshot? quota;
    private UsageHistory? history;
    private DateTimeOffset nextQuota, nextHistory;
    private int quotaFailures, historyFailures;
    private string? ReadCredential()
    {
        var value = secrets.Read();
        if (value != credential) { credential = value; quota = null; history = null; nextQuota = nextHistory = default; quotaFailures = historyFailures = 0; }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    public async Task<ProviderSnapshot> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var key = ReadCredential();
            if (key is null) return Empty() with { ConnectionStatus = ConnectionStatus.Unavailable, NeedsSetup = true, ErrorMessage = "请在设置中保存 OpenCode Console 只读 API Key。" };
            var now = time.GetUtcNow();
            // Force never bypasses an endpoint's cooldown or Retry-After.
            if (now < nextQuota && quota is not null) return quota;
            try
            {
                var body = await GetAsync(UsageUrl, key, cancellationToken).ConfigureAwait(false);
                quota = ParseQuota(body, now); quotaFailures = 0;
                nextQuota = now.AddSeconds(Math.Clamp(settings.GoRefreshSeconds, 180, 3600));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ProviderException or JsonException)
            {
                nextQuota = RetryAt(ex, now, ++quotaFailures, 180);
                quota = (quota ?? Empty()) with { ConnectionStatus = quota?.LastUpdated is null ? ConnectionStatus.Unavailable : ConnectionStatus.Stale, ErrorMessage = SafeError(ex) };
            }
            return quota;
        }
        finally { gate.Release(); }
    }
    public async Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous, HistoryRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var key = ReadCredential();
            if (key is null) return UsageHistory.Empty(Id) with { Error = "未配置 OpenCode Go API Key。" };
            var now = time.GetUtcNow();
            if (now < nextHistory && history is not null) return history;
            try
            {
                var csv=await GetAsync(ExportUrl,key,ct).ConfigureAwait(false);
                history=await Task.Run(()=>OpenCodeGoExport.Parse(csv,now,ct),ct).ConfigureAwait(false); historyFailures = 0;
                nextHistory = now.AddMinutes(30);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ProviderException)
            {
                nextHistory = RetryAt(ex, now, ++historyFailures, 1800);
                history = (history ?? UsageHistory.Empty(Id)) with { Error = SafeError(ex) };
            }
            return history;
        }
        finally { gate.Release(); }
    }
    private static ProviderSnapshot Empty() => ProviderSnapshot.Empty("opencode-go", "OpenCode Go", MetricType.Quota);
    public static ProviderSnapshot ParseQuota(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) throw new ProviderException("OpenCode Go 额度响应格式变化。");
        var windows = new List<QuotaWindow>();
        foreach (var (key, label, duration) in new[] { ("rolling", "5 小时", (long?)300), ("weekly", "每周", (long?)10080), ("monthly", "每月", (long?)null) })
        {
            if (!usage.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object ||
                !w.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("ok" or "rate-limited") ||
                !w.TryGetProperty("percent", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var used) || !double.IsFinite(used) || used is < 0 or > 100 ||
                !w.TryGetProperty("resetsAt", out var reset) || reset.ValueKind != JsonValueKind.String ||
                !(reset.GetString()!.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(reset.GetString()!, @"[+-]\d{2}:\d{2}$")) || !reset.TryGetDateTimeOffset(out var at)) continue;
            windows.Add(new(key, label, used, duration, at, status.GetString()));
        }
        if (windows.Count == 0) throw new ProviderException("OpenCode Go 未返回可核实的额度窗口。");
        return Empty() with { ConnectionStatus = windows.Count == 3 ? ConnectionStatus.Ready : ConnectionStatus.Partial,
            Windows = windows, RemainingPercent = windows.FirstOrDefault(w => w.Bucket == "rolling")?.RemainingPercent,
            LastUpdated = now, DataSource = "OpenCode Go · 服务端额度", ErrorMessage = windows.Count == 3 ? null : "部分额度窗口缺失或格式变化。" };
    }
    private sealed class ApiFailure(int status, DateTimeOffset? retry) : ProviderException("OpenCode Go HTTP " + status + "。")
    { public DateTimeOffset? Retry { get; } = retry; }
    private async Task<string> GetAsync(string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.UserAgent.ParseAdd("QuotaMonitor/1.7.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            throw new ApiFailure((int)response.StatusCode, retry?.Date ?? (retry?.Delta is {} delta ? time.GetUtcNow() + delta : null));
        }
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is not null && (url == UsageUrl ? media != "application/json" : media is not ("text/csv" or "application/csv" or "application/octet-stream")))
            throw new ProviderException("OpenCode Go 接口内容类型变化。");
        if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new ProviderException("OpenCode Go 响应超出大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
        { if (buffer.Length + count > 16 * 1024 * 1024) throw new ProviderException("OpenCode Go 响应超出大小限制。"); await buffer.WriteAsync(bytes.AsMemory(0, count), timeout.Token); }
        try{return new UTF8Encoding(false,true).GetString(buffer.ToArray());}catch(DecoderFallbackException){throw new ProviderException("OpenCode Go 响应不是有效 UTF-8。");}
    }
    private static DateTimeOffset RetryAt(Exception ex, DateTimeOffset now, int failures, int seconds)
    {
        var fallback = now.AddSeconds(Math.Min(21600, seconds * Math.Pow(2, Math.Min(failures - 1, 4))));
        return ex is ApiFailure { Retry: {} retry } && retry > fallback ? retry : fallback;
    }
    private static string SafeError(Exception ex) => ex is ProviderException ? ex.Message : ex is OperationCanceledException ? "OpenCode Go 请求超时。" : "OpenCode Go 网络或响应读取失败。";
}
