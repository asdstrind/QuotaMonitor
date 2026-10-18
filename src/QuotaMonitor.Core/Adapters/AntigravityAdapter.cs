using System.Globalization;
using System.Text.Json;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Core.Adapters;

// Weekly quota only. Quota changes are never converted into token usage.
public sealed class AntigravityAdapter(Func<CancellationToken, Task<string>>? read = null, TimeProvider? clock = null) : IProviderAdapter
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<CancellationToken, Task<string>> readLocal = read ?? AntigravityLocalClient.ReadAsync;
    private ProviderSnapshot? last;
    public string Id => "antigravity";
    public async Task<ProviderSnapshot> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            var body = await readLocal(cancellationToken);
            var snapshot = Parse(body, time.GetUtcNow());
            // Missing buckets/account state must never inherit a previous identity's quota.
            last = snapshot.Windows.Count > 0 ? snapshot : null;
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (ex is AntigravityReadException { AuthenticationFailed: true }) last = null;
            var safe = ex is AntigravityReadException e ? e.Message : "本地额度读取失败；响应与凭证已隐藏。";
            return last is { } previous
                ? previous with { ConnectionStatus = ConnectionStatus.Stale, ErrorMessage = safe }
                : Empty(ConnectionStatus.Unavailable, safe);
        }
    }

    private static ProviderSnapshot Empty(ConnectionStatus status, string? error) => new()
    {
        ProviderId = "antigravity", DisplayName = "Antigravity", MetricType = MetricType.Quota,
        ConnectionStatus = status, ErrorMessage = error, DataSource = "Antigravity local Language Server · RetrieveUserQuotaSummary",
        Note = "仅两项 Weekly 额度池；本机 Token 历史由独立通道读取。本地读取时间不代表服务端数据生成时间。"
    };

    public static ProviderSnapshot Parse(string body, DateTimeOffset observedAt)
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 });
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ToString() is not ("0" or "OK" or "SUCCESS"))
            return Empty(ConnectionStatus.Unavailable, "本地额度服务未返回成功状态。");
        var payload = root.TryGetProperty("response", out var response) ? response : root.TryGetProperty("summary", out var summary) ? summary : root;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return Empty(ConnectionStatus.Unavailable, "本地额度服务未返回 Weekly 分组。");
        var windows = new List<QuotaWindow>();
        foreach (var (id, name) in new[] { ("gemini-weekly", "Gemini"), ("3p-weekly", "Claude/GPT") })
        {
            // Exact bucket IDs, explicit weekly window, and exactly one matching record.
            var matches = groups.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object && g.TryGetProperty("buckets", out var b) && b.ValueKind == JsonValueKind.Array)
                .SelectMany(g => g.GetProperty("buckets").EnumerateArray())
                .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("bucketId", out var key) && key.GetString() == id).ToArray();
            if (matches.Length != 1) continue;
            var bucket = matches[0];
            if (!bucket.TryGetProperty("window", out var window) || window.GetString() != "weekly" ||
                bucket.TryGetProperty("disabled", out var disabled) && disabled.ValueKind != JsonValueKind.False) continue;
            var hasFraction = bucket.TryGetProperty("remainingFraction", out var fraction);
            if (!hasFraction && bucket.TryGetProperty("remaining", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                hasFraction = nested.TryGetProperty("remainingFraction", out fraction);
                if (!hasFraction && nested.TryGetProperty("case", out var which) && which.GetString() == "remainingFraction")
                    hasFraction = nested.TryGetProperty("value", out fraction);
            }
            if (!hasFraction || fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out var remaining) || !double.IsFinite(remaining) || remaining is < 0 or > 1) continue;
            DateTimeOffset? reset = null;
            if (bucket.TryGetProperty("resetTime", out var resetField) && resetField.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(resetField.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r) && r > observedAt) reset = r;
            windows.Add(new(id, name, 100 - remaining * 100, 10080, reset));
        }
        return Empty(windows.Count == 2 ? ConnectionStatus.Ready : windows.Count == 1 ? ConnectionStatus.Partial : ConnectionStatus.Unavailable,
            windows.Count < 2 ? "部分或全部 Weekly 额度缺失、未生效或无法核验。" : null) with
        {
            Windows = windows, LastUpdated = windows.Count > 0 ? observedAt : null,
            RemainingPercent = windows.Count > 0 ? windows.Min(w => w.RemainingPercent) : null
        };
    }
}
