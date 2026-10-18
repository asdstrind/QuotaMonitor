namespace QuotaMonitor.Core.Models;

public enum ConnectionStatus { Loading, Ready, Partial, Unavailable, Disabled, Stale }
public enum MetricType { Quota, Balance, Tokens }
public sealed record QuotaWindow(string Bucket, string Name, double UsedPercent, long? DurationMinutes, DateTimeOffset? ResetAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
public sealed record RequestLog(string Model, long? InputTokens, long? OutputTokens, decimal? UsageCost, DateTimeOffset? At, string? BillingSource);
public sealed record ProviderSnapshot
{
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public ConnectionStatus ConnectionStatus { get; init; } = ConnectionStatus.Loading;
    public MetricType MetricType { get; init; }
    public double? RemainingPercent { get; init; }
    public decimal? RemainingBalance { get; init; }
    public decimal? TodayCost { get; init; }
    public decimal? TotalCost { get; init; }
    public string? Currency { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TodayTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long? TodayRequests { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public DateTimeOffset? UsageUpdated { get; init; }
    public DshBalanceReading? AccountBalance { get; init; }
    public string DataSource { get; init; } = "";
    public bool IsEstimated { get; init; }
    public string? ErrorMessage { get; init; }
    // True when the provider cannot be queried until the user completes Settings (not a transient failure).
    public bool NeedsSetup { get; init; }
    public string? Note { get; init; }
    public string? CurrentModel { get; init; }
    public string? BillingPreference { get; init; }
    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];
    public IReadOnlyList<RequestLog> RecentRequests { get; init; } = [];
    public static ProviderSnapshot Empty(string id, string name, MetricType type) => new() { ProviderId=id, DisplayName=name, MetricType=type };
}
