using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;

namespace QuotaMonitor.Core.Services;

public interface IUsageHistorySource
{
    string Id { get; }
    Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous, HistoryRequest request, CancellationToken ct);
}

public sealed class CodexHistorySource(Func<string>? home = null, TimeZoneInfo? zone = null) : IUsageHistorySource
{
    private readonly CodexUsageHistory reader = new(zone);
    public string Id => "codex";
    public Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous, HistoryRequest request, CancellationToken ct) =>
        Task.Run(() => reader.Read((home ?? CodexUsageHistory.DefaultHome)(), request.Today, ct), ct);
}

/// <summary>
/// Schedules daily-usage history independently from quota/balance refresh: a slow or failing
/// history source never delays live values, and a failure keeps the last good history.
/// </summary>
public sealed class HistoryCoordinator : IAsyncDisposable
{
    private sealed class Slot(IUsageHistorySource source, UsageHistory? cached)
    {
        public IUsageHistorySource Source { get; } = source;
        public UsageHistory? History { get; set; } = cached;
        public DateTimeOffset Next { get; set; } = DateTimeOffset.MinValue;
        public int Failures { get; set; }
        public SemaphoreSlim Gate { get; } = new(1);
    }
    private readonly MonitorSettings settings;
    private readonly Slot[] slots;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    public int Weeks { get; }
    public event Action<UsageHistory>? Updated;

    public HistoryCoordinator(MonitorSettings settings, IEnumerable<IUsageHistorySource> sources, IEnumerable<UsageHistory>? cached = null, int weeks = UsageGrid.DefaultWeeks, TimeProvider? clock = null)
    {
        this.settings = settings; Weeks = weeks; this.clock = clock ?? TimeProvider.System;
        slots = sources.Select(s => new Slot(s, cached?.FirstOrDefault(h => h.ProviderId == s.Id))).ToArray();
    }

    public IReadOnlyList<UsageHistory> Histories => slots.Select(s => s.History).OfType<UsageHistory>().ToArray();
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.Local).DateTime);

    public void Start() { loop = RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            await RefreshAsync(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
            while (await timer.WaitForNextTickAsync(stop.Token)) await RefreshAsync(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public Task RefreshAsync(bool force) => Task.WhenAll(slots.Select(s => RefreshSlotAsync(s, force)));

    public static TimeSpan Interval(string id) => id switch { "claude" => TimeSpan.FromMinutes(5), "codex" => TimeSpan.FromMinutes(3), _ => TimeSpan.FromMinutes(2) };

    private async Task RefreshSlotAsync(Slot slot, bool force)
    {
        if (!await slot.Gate.WaitAsync(0, stop.Token)) return;
        try
        {
            var id = slot.Source.Id;
            if (!settings.Enabled(id))
            {
                if (slot.History?.Error != Disabled) { slot.History = UsageHistory.Empty(id) with { Error = Disabled, UpdatedAt = clock.GetUtcNow() }; Updated?.Invoke(slot.History); }
                slot.Next = DateTimeOffset.MinValue;
                return;
            }
            var now = clock.GetUtcNow();
            if (!force && now < slot.Next) return;
            var today = Today;
            var request = new HistoryRequest(today, UsageGrid.WindowStart(today, Weeks), force);
            var previous = slot.History?.Error == Disabled ? null : slot.History;
            try
            {
                slot.History = await slot.Source.ReadHistoryAsync(previous, request, stop.Token);
                slot.Failures = slot.History.Error is null ? 0 : slot.Failures + 1;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                var safe = ex is ProviderException ? ex.Message : ex is OperationCanceledException ? "历史读取超时。" : ex is HttpRequestException ? "网络连接失败。" : "历史读取失败（" + ex.GetType().Name + "），详情已隐藏。";
                slot.History = (previous ?? UsageHistory.Empty(id)) with { Error = safe };
                slot.Failures++;
            }
            // Backfill continues quickly while days are pending; errors back off like live refresh.
            var interval = slot.History.PendingDays > 0 && slot.History.Error is null ? TimeSpan.FromSeconds(45) : Interval(id);
            slot.Next = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(1800, interval.TotalSeconds * Math.Pow(2, Math.Min(slot.Failures, 3))));
            Updated?.Invoke(slot.History);
        }
        finally { slot.Gate.Release(); }
    }

    public const string Disabled = "已在设置中关闭";

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) try { await loop; } catch (OperationCanceledException) { }
        foreach (var s in slots) { await s.Gate.WaitAsync(); s.Gate.Release(); s.Gate.Dispose(); }
        stop.Dispose();
    }
}
