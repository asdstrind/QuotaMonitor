namespace QuotaMonitor.Core.Models;

public enum LaneState { Missing, Zero, Exact, Estimated, Partial }

// One provider within one day. Level uses the shared scale, RelativeLevel the provider's own scale.
public sealed record GridLane(string ProviderId, LaneState State, long? Tokens, long? Requests, decimal? Cost, long? Cached, double Level, double RelativeLevel, int Excluded = 0)
{
    public bool HasUsage => Tokens > 0 && State is LaneState.Exact or LaneState.Estimated or LaneState.Partial;
}
public sealed record GridDay(DateOnly Date, int Column, int Row, bool IsToday, IReadOnlyList<GridLane> Lanes)
{
    public GridLane? Dominant => Lanes.Where(l => l.HasUsage).OrderByDescending(l => l.Tokens).FirstOrDefault();
    public long? Total => SumKnown(Lanes.Select(l => l.Tokens));
    public bool Incomplete => Lanes.Count == 0 || Lanes.Any(l => l.State is LaneState.Missing or LaneState.Partial || l.Excluded > 0);
    internal static long? SumKnown(IEnumerable<long?> values)
    {
        var known = values.OfType<long>().ToArray();
        if (known.Length == 0) return null;
        try { return known.Aggregate(0L, (sum, value) => checked(sum + value)); } catch (OverflowException) { return null; }
    }
}
public sealed record GridProvider(string Id, long? Reference, long Tokens, long Requests, decimal? Cost, int ActiveDays, int CoveredDays, DateOnly? CoverageStart, bool HasEstimated, bool HasPartial, UsageHistory? History);
public sealed record GridMonth(int Column, DateOnly Month);

/// <summary>
/// Monday-first week-column calendar over a fixed window. Combined square intensity is
/// log1p of the daily total, clipped at the enabled daily totals' P95. Single-provider preview
/// uses its own daily P95. Legacy lane levels remain available to diagnostics.
/// </summary>
public sealed record UsageGrid(DateOnly Start, DateOnly Today, int Weeks, IReadOnlyList<GridDay> Days, IReadOnlyList<GridProvider> Providers, IReadOnlyList<GridMonth> Months, long? SharedReference)
{
    public const int DefaultWeeks = 26;
    public const double SharedDecades = 4;
    public const double RelativeDecades = 3;
    public const double MinLevel = 0.06;
    public static readonly IReadOnlyList<string> Order = ["codex", "claude", "dsh", "antigravity"];

    public static DateOnly WindowStart(DateOnly today, int weeks)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return monday.AddDays(-7 * (weeks - 1));
    }

    public static double Level(long tokens, long reference, double decades) =>
        tokens <= 0 ? 0 : reference <= 0 ? 1 : Math.Clamp(1 + Math.Log10((double)tokens / reference) / decades, MinLevel, 1);
    // Winsorize at the selected window's P95; log1p opens up the low/mid range.
    // One reference for enabled daily totals, one reference for a focused Harness.
    public static double TileLevel(long tokens, long reference) => tokens <= 0 ? 0 :
        reference <= 0 ? 1 : .08 + .92 * Math.Log10(1 + 9 * Math.Min(1, (double)tokens / reference));

    public static long? Percentile95(IReadOnlyCollection<long> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    public GridDay? DayAt(int column, int row) =>
        column < 0 || row is < 0 or > 6 || column >= Weeks ? null : Days.ElementAtOrDefault(column * 7 + row);

    public static UsageGrid Build(IEnumerable<UsageHistory?> histories, DateOnly today, int weeks = DefaultWeeks, IEnumerable<string>? visible = null)
    {
        weeks = Math.Clamp(weeks, 1, 104);
        var selected = (visible ?? Order).ToHashSet();
        var order = Order.Where(selected.Contains).ToArray();
        var start = WindowStart(today, weeks);
        var map = histories.OfType<UsageHistory>().GroupBy(h => h.ProviderId).ToDictionary(g => g.Key, g => g.Last());
        var lookups = order.ToDictionary(id => id, id => map.TryGetValue(id, out var h)
            ? h.Days.Where(d => d.Date >= start && d.Date <= today).GroupBy(d => d.Date).ToDictionary(g => g.Key, Deduplicate)
            : new Dictionary<DateOnly, UsageDay>());
        var providers = new List<GridProvider>();
        var pooled = new List<long>();
        foreach (var id in order)
        {
            var days = lookups[id].Values.ToArray();
            var positive = days.Where(d => d.Tokens > 0).Select(d => d.Tokens!.Value).ToArray();
            // A window cost is only shown when every covered day carries one.
            decimal? cost = days.Length > 0 && days.All(d => d.Cost is not null) ? days.Sum(d => d.Cost!.Value) : null;
            providers.Add(new GridProvider(id, Percentile95(positive), days.Sum(d => d.Tokens ?? 0), days.Sum(d => d.Requests ?? 0), cost, positive.Length, days.Length,
                days.Length > 0 ? days.Min(d => d.Date) : null, days.Any(d => d.Quality == UsageQuality.Estimated && d.Tokens > 0), days.Any(d => d.Quality == UsageQuality.Partial), map.GetValueOrDefault(id)));
        }
        // One intensity axis from the enabled daily TOTALS, never independently normalized lanes.
        foreach (var date in lookups.Values.SelectMany(l => l.Keys).Distinct())
            if (GridDay.SumKnown(order.Select(id => lookups[id].GetValueOrDefault(date)?.Tokens)) is > 0 and var total) pooled.Add(total);
        var shared = Percentile95(pooled);
        var cells = new List<GridDay>();
        for (var date = start; date <= today; date = date.AddDays(1))
        {
            var offset = date.DayNumber - start.DayNumber;
            var lanes = providers.Select(p => Lane(p.Id, lookups[p.Id].GetValueOrDefault(date), shared, p.Reference)).ToArray();
            cells.Add(new GridDay(date, offset / 7, offset % 7, date == today, lanes));
        }
        var months = new List<GridMonth>();
        for (var column = 0; column < weeks; column++)
        {
            var monday = start.AddDays(column * 7);
            if (monday.Day <= 7) months.Add(new(column, new DateOnly(monday.Year, monday.Month, 1)));
        }
        // The partial first month is labelled only when the next label leaves room for it.
        if (months.Count == 0 || months[0].Column >= 3) months.Insert(0, new(0, new DateOnly(start.Year, start.Month, 1)));
        return new UsageGrid(start, today, weeks, cells, providers, months, shared);
    }

    private static UsageDay Deduplicate(IGrouping<DateOnly, UsageDay> records)
    {
        var latest = records.OrderByDescending(d => d.FetchedAt).First();
        var peers = records.Where(d => d.FetchedAt == latest.FetchedAt).Distinct().ToArray();
        // Identical cache copies count once; contradictory peers cannot establish a daily amount.
        return peers.Length == 1 ? latest : latest with { Tokens = null, Quality = UsageQuality.Partial, Excluded = Math.Max(1, latest.Excluded) };
    }

    private static GridLane Lane(string id, UsageDay? day, long? shared, long? own)
    {
        if (day is null) return new(id, LaneState.Missing, null, null, null, null, 0, 0);
        if (day.Tokens is not { } tokens) return new(id, LaneState.Missing, null, day.Requests, day.Cost, day.CachedTokens, 0, 0, day.Excluded);
        var state = day.Quality switch
        {
            UsageQuality.Partial => LaneState.Partial,
            UsageQuality.Estimated when tokens > 0 => LaneState.Estimated,
            _ => tokens == 0 ? LaneState.Zero : LaneState.Exact
        };
        return new(id, state, tokens, day.Requests, day.Cost, day.CachedTokens, Level(tokens, shared ?? 0, SharedDecades), Level(tokens, own ?? 0, RelativeDecades), day.Excluded);
    }
}
