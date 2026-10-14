namespace QuotaMonitor.Core.Models;

// How a stored provider-day was measured. Days a source cannot vouch for are not stored at all,
// so "missing" is never confused with a recorded zero.
public enum UsageQuality { Exact, Estimated, Partial }

// Excluded counts records of that day that could not be verified (they are never guessed into Tokens).
public sealed record UsageDay(DateOnly Date, long? Tokens, long? Requests, decimal? Cost, UsageQuality Quality, DateTimeOffset? FetchedAt = null, long? CachedTokens = null, int Excluded = 0);

public sealed record UsageHistory
{
    public required string ProviderId { get; init; }
    // Every covered day is listed explicitly, including zero days.
    public IReadOnlyList<UsageDay> Days { get; init; } = [];
    public DateOnly? CoverageStart { get; init; }
    public string Source { get; init; } = "";
    public string Scope { get; init; } = "";
    public string? Note { get; init; }
    public string? Error { get; init; }
    // Non-secret account key; history from a different account is never merged.
    public string? Account { get; init; }
    public string? CostCurrency { get; init; }
    public bool CostEstimated { get; init; }
    public int PendingDays { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public static UsageHistory Empty(string id) => new() { ProviderId = id };
}

public sealed record HistoryRequest(DateOnly Today, DateOnly WindowStart, bool Force);
