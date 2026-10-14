using System.Net;
using System.Text;
using System.Text.Json;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;
using QuotaMonitor.ViewModels;
using ZstdSharp;

namespace QuotaMonitor.Tests;

// Daily history: Codex rollouts, DSH sessions, ClaudeCN statistics, the calendar grid and its view model.
public sealed class HistoryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Today = new(2026, 10, 10);
    private static string Rows(params object[] rows) => string.Join('\n', rows.Select(x => JsonSerializer.Serialize(x))) + "\n";
    private static string TempDir(string name) { var d = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid()); Directory.CreateDirectory(d); return d; }
    private static long Ms(int day, int hour = 12) => new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static string Iso(int day, int hour = 12, int minute = 0) => new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    // ---- DSH inherited-history semantics (installed 0.2.0-rc.2 persistence format) ----
    private static object Ctx(long time) => new { type = "request/context", seq = 1, time, data = new { provider = "deepseek-account", model = "deepseek-flash" } };
    private static object Msg(int seq, int step, int input, int output, long time) => new { type = "assistant/message", seq, time, data = new { turn = 1, step, usage = new { inputTokens = input, outputTokens = output, cacheReadTokens = 0, cacheWriteTokens = 0 } } };
    private static object EndSeed(int seq, long time, bool inherited) => inherited ? new { type = "session/end-seed", seq, time, data = (object)new { inherited = true } } : new { type = "session/end-seed", seq, time, data = (object)new { } };

    [Fact] public void UntaggedRestoreMarkerKeepsTheSessionsOwnEarlierUsage()
    {
        var session = DshAdapter.ParseLines(Rows(new { type = "session", version = 4, id = "reopened", isSeeded = false }, Ctx(Ms(1)), Msg(2, 1, 100, 50, Ms(1)), EndSeed(3, Ms(2), false), Msg(4, 2, 200, 60, Ms(2))));
        Assert.Equal(410, session.Usage.Sum(u => u.Total)); Assert.Equal(0, session.InvalidRows);
    }
    [Fact] public void SeededSessionCutsAtTheLastInheritedMarkerNotAtLaterRestoreMarkers()
    {
        var session = DshAdapter.ParseLines(Rows(new { type = "session", version = 4, id = "child", isSeeded = true }, Ctx(Ms(1)), Msg(2, 1, 1000, 1000, Ms(1)), EndSeed(3, Ms(1), true), Msg(4, 2, 100, 50, Ms(2)), EndSeed(5, Ms(3), false), Msg(6, 3, 200, 60, Ms(3))));
        Assert.Equal(410, session.Usage.Sum(u => u.Total));
    }
    [Fact] public void SeededSessionWithOnlyRestoreMarkersIsIncomplete()
    {
        var session = DshAdapter.ParseLines(Rows(new { type = "session", version = 4, id = "child", isSeeded = true }, Ctx(Ms(1)), Msg(2, 1, 100, 50, Ms(1)), EndSeed(3, Ms(2), false), Msg(4, 2, 200, 60, Ms(2))));
        Assert.Empty(session.Usage); Assert.Equal(1, session.InvalidRows);
    }
    [Fact] public void UnseededSessionCarryingAnInheritedMarkerIsRejected()
    {
        var session = DshAdapter.ParseLines(Rows(new { type = "session", version = 4, id = "odd", isSeeded = false }, Ctx(Ms(1)), Msg(2, 1, 100, 50, Ms(1)), EndSeed(3, Ms(2), true), Msg(4, 2, 200, 60, Ms(2))));
        Assert.Empty(session.Usage); Assert.Equal(1, session.InvalidRows);
    }
    [Fact] public void ReleasedV0WithoutSeedLengthCountsUsageOnBothSidesOfRestoreMarkers()
    {
        var session = DshAdapter.ParseLines(Rows(new { type = "session", version = 0, id = "v0" }, Ctx(Ms(1)), Msg(2, 1, 100, 50, Ms(1)), EndSeed(3, Ms(2), false), Msg(4, 2, 200, 60, Ms(2))));
        Assert.Equal(410, session.Usage.Sum(u => u.Total));
    }
    [Fact] public void ReleasedV0SeedLengthCutsAtTheMarkerWithThatSequence()
    {
        var withMarker = DshAdapter.ParseLines(Rows(new { type = "session", version = 0, id = "v0-child", seedLength = 3 }, Ctx(Ms(1)), Msg(2, 1, 1000, 1000, Ms(1)), EndSeed(3, Ms(1), false), Msg(4, 2, 100, 50, Ms(2)), EndSeed(5, Ms(3), false), Msg(6, 3, 200, 60, Ms(3))));
        Assert.Equal(410, withMarker.Usage.Sum(u => u.Total));
        var withoutMarker = DshAdapter.ParseLines(Rows(new { type = "session", version = 0, id = "v0-child", seedLength = 3 }, Ctx(Ms(1)), Msg(2, 1, 1000, 1000, Ms(1)), Msg(4, 2, 100, 50, Ms(2))));
        Assert.Empty(withoutMarker.Usage); Assert.Equal(1, withoutMarker.InvalidRows);
    }

    [Fact] public void DshHistoryBucketsLocalDaysFillsZerosAndDatesExcludedAttempts()
    {
        var root = TempDir("quota-dsh-history");
        try
        {
            var a = Path.Combine(root, "--project--", "session-a"); Directory.CreateDirectory(a);
            File.WriteAllText(Path.Combine(a, "session.v4.jsonl"), Rows(new { type = "session", version = 4, id = "a", isSeeded = false }, Ctx(Ms(5)), Msg(2, 1, 100, 50, Ms(5)), Msg(3, 2, 300, 100, Ms(7))));
            var b = Path.Combine(root, "--project--", "session-b"); Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(b, "session.v4.jsonl"), Rows(new { type = "session", version = 4, id = "b", isSeeded = false }, Ctx(Ms(7)), new { type = "assistant/attempt", seq = 2, time = Ms(7), data = new { turn = 1, step = 1, stream = new object[0] } }, new { type = "llm/retry-started", seq = 3, time = Ms(7), data = new { turn = 1, step = 1 } }, Msg(4, 1, 10, 5, Ms(7))));
            var history = new DshAdapter(new MonitorSettings { DshSessionRoot = root }).ReadHistory(Today, Utc, CancellationToken.None);
            Assert.Equal(new DateOnly(2026, 10, 5), history.CoverageStart);
            var days = history.Days.ToDictionary(d => d.Date);
            Assert.Equal(6, days.Count);
            Assert.Equal(150, days[new(2026, 10, 5)].Tokens); Assert.Equal(UsageQuality.Exact, days[new(2026, 10, 5)].Quality);
            Assert.Equal(0, days[new(2026, 10, 6)].Tokens); Assert.Equal(UsageQuality.Exact, days[new(2026, 10, 6)].Quality);
            var seventh = days[new(2026, 10, 7)];
            Assert.Equal(415, seventh.Tokens); Assert.Equal(2, seventh.Requests); Assert.Equal(UsageQuality.Partial, seventh.Quality); Assert.Equal(1, seventh.Excluded);
            Assert.True(history.CostEstimated);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- Codex rollouts ----
    private static object Record(string response, string at, int input, int cached, int output) => new { timestamp = at, type = "token_usage_record", payload = new { response_id = response, usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, total_tokens = input + output } } };
    private static object Count(string at, long total, long last) => new { timestamp = at, type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { total_tokens = total }, last_token_usage = new { input_tokens = last, cached_input_tokens = 0, total_tokens = last } } } };
    private static string Rollout(string sessions, int day, string name) { var dir = Path.Combine(sessions, "2026", "10", day.ToString("00")); Directory.CreateDirectory(dir); return Path.Combine(dir, $"rollout-2026-10-{day:00}T08-00-00-{name}.jsonl"); }

    [Fact] public void CodexRecordsCountEachResponseOnceEvenAcrossCompressedCopies()
    {
        var home = TempDir("quota-codex-history");
        try
        {
            var file = Rollout(Path.Combine(home, "sessions"), 8, "a");
            var text = Rows(new { timestamp = Iso(8), type = "session_meta", payload = new { id = "a" } }, Record("r1", Iso(8), 1000, 800, 50), Count(Iso(8), 1050, 1050), Record("r2", Iso(9), 2000, 1500, 70), Count(Iso(9), 3120, 2070));
            File.WriteAllText(file, text);
            using (var compressor = new Compressor()) File.WriteAllBytes(file + ".zst", compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray());
            var history = new CodexUsageHistory(Utc).Read(home, Today, CancellationToken.None);
            var days = history.Days.ToDictionary(d => d.Date);
            Assert.Equal(new DateOnly(2026, 10, 8), history.CoverageStart);
            Assert.Equal(1050, days[new(2026, 10, 8)].Tokens); Assert.Equal(1, days[new(2026, 10, 8)].Requests); Assert.Equal(800, days[new(2026, 10, 8)].CachedTokens);
            Assert.Equal(2070, days[new(2026, 10, 9)].Tokens);
            Assert.Equal(0, days[Today].Tokens); Assert.Equal(UsageQuality.Exact, days[Today].Quality);
            Assert.DoesNotContain(history.Days, d => d.Date < new DateOnly(2026, 10, 8));
        }
        finally { Directory.Delete(home, true); }
    }
    [Fact] public void CodexLegacyCountersBecomePerResponseDeltasAndInconsistenciesAreEstimated()
    {
        var home = TempDir("quota-codex-legacy");
        try
        {
            File.WriteAllText(Rollout(Path.Combine(home, "sessions"), 3, "legacy"), Rows(Count(Iso(3, 8), 100, 100), Count(Iso(3, 9), 100, 100), Count(Iso(3, 10), 250, 150), Count(Iso(4, 8), 240, 40), Count(Iso(5, 8), 300, 70)));
            var days = new CodexUsageHistory(Utc).Read(home, Today, CancellationToken.None).Days.ToDictionary(d => d.Date);
            Assert.Equal(250, days[new(2026, 10, 3)].Tokens); Assert.Equal(2, days[new(2026, 10, 3)].Requests); Assert.Equal(UsageQuality.Exact, days[new(2026, 10, 3)].Quality);
            Assert.Equal(40, days[new(2026, 10, 4)].Tokens); Assert.Equal(UsageQuality.Estimated, days[new(2026, 10, 4)].Quality);
            Assert.Equal(60, days[new(2026, 10, 5)].Tokens); Assert.Equal(UsageQuality.Estimated, days[new(2026, 10, 5)].Quality);
        }
        finally { Directory.Delete(home, true); }
    }
    [Fact] public void CodexAppendOnlyRolloutIsReadIncrementallyAndHalfWrittenLinesWait()
    {
        var home = TempDir("quota-codex-append");
        try
        {
            var file = Rollout(Path.Combine(home, "sessions"), 9, "live");
            File.WriteAllText(file, Rows(Record("r1", Iso(9, 8), 100, 0, 10)));
            var reader = new CodexUsageHistory(Utc);
            Assert.Equal(110, reader.Read(home, Today, CancellationToken.None).Days.Single(d => d.Date == new DateOnly(2026, 10, 9)).Tokens);
            var next = JsonSerializer.Serialize(Record("r2", Iso(9, 9), 200, 0, 20));
            File.AppendAllText(file, next[..20]);
            Assert.Equal(110, reader.Read(home, Today, CancellationToken.None).Days.Single(d => d.Date == new DateOnly(2026, 10, 9)).Tokens);
            File.AppendAllText(file, next[20..] + "\n");
            var day = reader.Read(home, Today, CancellationToken.None).Days.Single(d => d.Date == new DateOnly(2026, 10, 9));
            Assert.Equal(330, day.Tokens); Assert.Equal(2, day.Requests);
        }
        finally { Directory.Delete(home, true); }
    }
    [Fact] public void CodexUnparseableUsageMarksItsDayPartialWithoutGuessing()
    {
        var home = TempDir("quota-codex-broken");
        try
        {
            File.WriteAllText(Rollout(Path.Combine(home, "sessions"), 6, "broken"), Rows(Record("r1", Iso(6), 100, 0, 10)) + "{\"timestamp\":\"" + Iso(6) + "\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":\n");
            var day = new CodexUsageHistory(Utc).Read(home, Today, CancellationToken.None).Days.Single(d => d.Date == new DateOnly(2026, 10, 6));
            Assert.Equal(110, day.Tokens); Assert.Equal(UsageQuality.Partial, day.Quality); Assert.Equal(1, day.Excluded);
        }
        finally { Directory.Delete(home, true); }
    }
    [Fact] public void MissingCodexHomeIsAnErrorNotAnEmptyHistory()
    {
        var history = new CodexUsageHistory(Utc).Read(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()), Today, CancellationToken.None);
        Assert.Empty(history.Days); Assert.NotNull(history.Error);
    }

    // ---- ClaudeCN statistics per local day ----
    private sealed class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => zone;
    }
    private sealed class Recorder(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Secret(string? value) : ISecretStore { public string? Read() => value; public void Write(string v) { } public void Delete() { } }
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);

    [Fact] public async Task ClaudeHistoryBackfillsLocalDaysInBoundedBatches()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var time = new FixedTime(new DateTimeOffset(2026, 10, 10, 15, 30, 0, TimeSpan.FromHours(8)), zone);
        var handler = new Recorder(_ => """{"success":true,"data":{"quota":250000,"total_tokens":1200,"total_requests":3}}""");
        using var http = new HttpClient(handler);
        var adapter = new ClaudeCNAdapter(http, new MonitorSettings { ClaudeUserId = "42" }, new Secret("test-only"), time);
        var request = new HistoryRequest(Today, Today.AddDays(-39), false);
        var first = await adapter.ReadHistoryAsync(null, request, CancellationToken.None);
        Assert.Equal(ClaudeCNAdapter.HistoryBatch, handler.Calls.Count);
        Assert.Equal(40 - ClaudeCNAdapter.HistoryBatch, first.PendingDays);
        Assert.All(handler.Calls, c => { Assert.Equal("/api/log/self/stat", c.AbsolutePath); Assert.Equal("2", Query(c)["type"]); });
        // Today runs from local midnight to now; earlier days cover exactly one local calendar day.
        var today = Query(handler.Calls[0]);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds().ToString(), today["start_timestamp"]);
        Assert.Equal(time.Now.ToUnixTimeSeconds().ToString(), today["end_timestamp"]);
        var yesterday = Query(handler.Calls[1]);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds().ToString(), yesterday["start_timestamp"]);
        Assert.Equal((new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds() - 1).ToString(), yesterday["end_timestamp"]);
        var day = first.Days.Single(d => d.Date == Today);
        Assert.Equal(1200, day.Tokens); Assert.Equal(3, day.Requests); Assert.Equal(0.5m, day.Cost); Assert.Equal(UsageQuality.Exact, day.Quality);
        handler.Calls.Clear();
        var second = await adapter.ReadHistoryAsync(first, request, CancellationToken.None);
        Assert.Equal(40 - ClaudeCNAdapter.HistoryBatch, handler.Calls.Count);
        Assert.Equal(0, second.PendingDays); Assert.Equal(40, second.Days.Count);
        handler.Calls.Clear();
        time.Now = time.Now.AddMinutes(5);
        await adapter.ReadHistoryAsync(second, request, CancellationToken.None);
        Assert.Single(handler.Calls);
    }
    [Fact] public async Task ClaudeHistoryRereadsADayFetchedBeforeItEndedAndForgetsOtherAccounts()
    {
        var zone = TimeZoneInfo.Utc;
        var time = new FixedTime(new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.Zero), zone);
        var handler = new Recorder(_ => """{"success":true,"data":{"quota":0,"total_tokens":0,"total_requests":0}}""");
        using var http = new HttpClient(handler);
        var settings = new MonitorSettings { ClaudeUserId = "42" };
        var adapter = new ClaudeCNAdapter(http, settings, new Secret("test-only"), time);
        var request = new HistoryRequest(Today, Today.AddDays(-1), false);
        var account = "claudecn:42:500000";
        var previous = new UsageHistory { ProviderId = "claude", Account = account, Days = [new UsageDay(Today.AddDays(-1), 10, 1, 0m, UsageQuality.Exact, new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero)), new UsageDay(Today, 5, 1, 0m, UsageQuality.Exact, time.Now)] };
        var result = await adapter.ReadHistoryAsync(previous, request, CancellationToken.None);
        Assert.Single(handler.Calls);
        Assert.Equal(0, result.Days.Single(d => d.Date == Today.AddDays(-1)).Tokens);
        handler.Calls.Clear();
        settings.ClaudeUserId = "43";
        var other = await adapter.ReadHistoryAsync(result, request, CancellationToken.None);
        Assert.Equal(2, handler.Calls.Count); Assert.Equal("claudecn:43:500000", other.Account);
    }
    [Fact] public async Task ClaudeHistoryNeverStoresMissingStatisticsAsZero()
    {
        var handler = new Recorder(_ => """{"success":true,"data":{}}""");
        using var http = new HttpClient(handler);
        var result = await new ClaudeCNAdapter(http, new MonitorSettings { ClaudeUserId = "42" }, new Secret("test-only")).ReadHistoryAsync(null, new HistoryRequest(Today, Today.AddDays(-5), false), CancellationToken.None);
        Assert.Empty(result.Days); Assert.NotNull(result.Error); Assert.Single(handler.Calls);
    }
    [Fact] public async Task ClaudeHistoryWithoutCredentialsDoesNotTouchTheNetwork()
    {
        var handler = new Recorder(_ => throw new InvalidOperationException("must not call"));
        using var http = new HttpClient(handler);
        var result = await new ClaudeCNAdapter(http, new MonitorSettings(), new Secret(null)).ReadHistoryAsync(null, new HistoryRequest(Today, Today.AddDays(-5), false), CancellationToken.None);
        Assert.Empty(handler.Calls); Assert.Empty(result.Days); Assert.NotNull(result.Error);
    }

    // ---- Calendar grid ----
    private static UsageHistory History(string id, params UsageDay[] days) => new() { ProviderId = id, Days = days, CoverageStart = days.Min(d => d.Date) };
    private static UsageDay D(int day, long tokens, UsageQuality q = UsageQuality.Exact, int excluded = 0) => new(new DateOnly(2026, 10, day), tokens, tokens > 0 ? 1 : 0, null, q, null, null, excluded);

    [Fact] public void GridAlignsWeeksToMondayAndKeepsMissingZeroEstimatedAndPartialApart()
    {
        var grid = UsageGrid.Build([History("codex", D(5, 1_000_000), D(6, 0), D(7, 500, UsageQuality.Estimated)), History("claude", D(7, 20_000, UsageQuality.Partial, 1))], Today);
        Assert.Equal(DayOfWeek.Monday, grid.Start.DayOfWeek); Assert.Equal(UsageGrid.DefaultWeeks, grid.Weeks); Assert.Equal(Today, grid.Days[^1].Date);
        GridLane Lane(int day, int index) => grid.Days.Single(d => d.Date == new DateOnly(2026, 10, day)).Lanes[index];
        Assert.Equal(LaneState.Exact, Lane(5, 0).State); Assert.Equal(LaneState.Zero, Lane(6, 0).State); Assert.Equal(LaneState.Estimated, Lane(7, 0).State);
        Assert.Equal(LaneState.Missing, Lane(5, 1).State); Assert.Equal(LaneState.Partial, Lane(7, 1).State); Assert.Equal(1, Lane(7, 1).Excluded);
        Assert.Equal(LaneState.Missing, Lane(7, 2).State);
        var monday = grid.Days.Single(d => d.Date == new DateOnly(2026, 10, 5));
        Assert.Equal(0, monday.Row); Assert.Same(monday, grid.DayAt(monday.Column, monday.Row));
        Assert.Null(grid.DayAt(grid.Weeks - 1, 6));
        Assert.Contains(grid.Months, m => m.Month == new DateOnly(2026, 10, 1));
    }
    [Fact] public void SharedScaleComparesPlatformsWhileRelativeScaleShowsEachRhythm()
    {
        var grid = UsageGrid.Build([History("codex", D(8, 10_000_000), D(9, 100_000_000)), History("claude", D(8, 10_000), D(9, 100_000))], Today);
        GridLane Lane(int day, int index) => grid.Days.Single(d => d.Date == new DateOnly(2026, 10, day)).Lanes[index];
        Assert.True(Lane(9, 0).Level > Lane(9, 1).Level);
        Assert.Equal(1, Lane(9, 0).RelativeLevel, 6); Assert.Equal(1, Lane(9, 1).RelativeLevel, 6);
        Assert.True(Lane(8, 1).RelativeLevel < 1 && Lane(8, 1).RelativeLevel > UsageGrid.MinLevel);
        // Ten times the tokens is a fixed step on the log axis.
        Assert.Equal(1 / UsageGrid.SharedDecades, Lane(9, 0).Level - Lane(8, 0).Level, 6);
        Assert.Equal(UsageGrid.MinLevel, UsageGrid.Level(1, 1_000_000_000, UsageGrid.SharedDecades));
        Assert.Equal(0, UsageGrid.Level(0, 100, UsageGrid.SharedDecades));
    }

    // ---- Coordinator ----
    private sealed class FlakySource(string id) : IUsageHistorySource
    {
        public int Calls;
        public string Id => id;
        public Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous, HistoryRequest request, CancellationToken ct) =>
            ++Calls == 1 ? Task.FromResult(History(id, D(9, 42))) : throw new ProviderException("intentional test failure");
    }
    [Fact] public async Task HistoryFailureKeepsTheLastGoodDaysAndDisabledPlatformsAreMarked()
    {
        var source = new FlakySource("codex");
        await using var coordinator = new HistoryCoordinator(new MonitorSettings { DshEnabled = false }, [source, new FlakySource("dsh")]);
        await coordinator.RefreshAsync(true);
        await coordinator.RefreshAsync(true);
        var codex = coordinator.Histories.Single(h => h.ProviderId == "codex");
        Assert.Equal(42, codex.Days.Single().Tokens); Assert.Equal("intentional test failure", codex.Error);
        Assert.Equal(HistoryCoordinator.Disabled, coordinator.Histories.Single(h => h.ProviderId == "dsh").Error);
    }

    // ---- View model ----
    [Fact] public void CompactNumbersAndAmountsFollowChineseReadingHabits()
    {
        Assert.Equal("8,450", Format.Tokens(8450)); Assert.Equal("5.1万", Format.Tokens(51_000)); Assert.Equal("8,523万", Format.Tokens(85_226_286)); Assert.Equal("16.79亿", Format.Tokens(1_679_265_853));
        Assert.Equal(("$", "12", ".34"), Format.Split("$12.34")); Assert.Equal(("¥", "1,234", ".56"), Format.Split("¥1,234.56")); Assert.Equal(("", "<¥0.01", ""), Format.Split("<¥0.01")); Assert.Equal(("", "¥6.91 + $1.00", ""), Format.Split("¥6.91 + $1.00"));
        Assert.Equal("<$0.01", Format.Usd(0.001m)); Assert.Equal("$0.00", Format.Usd(0m));
        Assert.Equal("5 小时", Format.Window(300)); Assert.Equal("每周", Format.Window(10080)); Assert.Equal("2 小时 14 分后重置", Format.Countdown(new TimeSpan(2, 14, 30)));
        Assert.Equal("10月5日 星期一", Format.Day(new DateOnly(2026, 10, 5)));
    }
    [Fact] public void CodexGaugesShowTheSelectedBucketsTwoWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = new ProviderViewModel(ProviderSnapshot.Empty("codex", "Codex", MetricType.Quota) with { ConnectionStatus = ConnectionStatus.Ready, LastUpdated = now, Windows = [new("other", "Primary", 10, 60, null), new("codex", "Secondary", 21, 10080, now.AddDays(3)), new("codex", "Primary", 88, 300, now.AddMinutes(90))] });
        Assert.Collection(vm.Gauges,
            g => { Assert.Equal("5 小时", g.Label); Assert.Equal("12", g.Value); Assert.True(g.Low); Assert.Contains("小时", g.Reset); },
            g => { Assert.Equal("每周", g.Label); Assert.Equal("79", g.Value); Assert.False(g.Low); });
        Assert.False(vm.StateIsWarning); Assert.Equal(1, vm.ValueOpacity);
        vm.Update(vm.Snapshot with { LastUpdated = now.AddMinutes(-30) });
        Assert.Equal("已过期", vm.StateText); Assert.True(vm.StateIsWarning); Assert.True(vm.ValueOpacity < 1); Assert.Equal("—", vm.Compact);
    }
    [Fact] public void ClaudeSetupStateAndBalanceReadout()
    {
        var setup = new ProviderViewModel(ProviderSnapshot.Empty("claude", "Claude", MetricType.Balance) with { ConnectionStatus = ConnectionStatus.Unavailable, NeedsSetup = true });
        Assert.Equal("未配置", setup.StateText); Assert.False(setup.HasAmount); Assert.Equal("—", setup.AmountMajor);
        var live = new ProviderViewModel(ProviderSnapshot.Empty("claude", "Claude", MetricType.Balance) with { ConnectionStatus = ConnectionStatus.Ready, LastUpdated = DateTimeOffset.UtcNow, RemainingBalance = 12.345678m, TodayCost = 0.37m, Currency = "USD" });
        Assert.Equal("$", live.AmountSymbol); Assert.Equal("12", live.AmountMajor); Assert.Equal(".35", live.AmountMinor); Assert.Equal("今日已用 $0.37", live.DetailLine);
        Assert.Contains("12.345678", live.Tooltip);
    }
    [Fact] public void DayTipSaysWhichPlatformIsMissingZeroEstimatedOrIncomplete()
    {
        var vm = new MonitorViewModel();
        vm.UpdateHistory(History("codex", D(7, 2_500_000, UsageQuality.Estimated)), Today);
        vm.UpdateHistory(History("claude", D(7, 0)), Today);
        vm.UpdateHistory(History("dsh", D(7, 36_000, UsageQuality.Partial, 2)), Today);
        var tip = vm.BuildTip(vm.Grid!.Days.Single(d => d.Date == new DateOnly(2026, 10, 7)));
        Assert.Equal("10月7日 星期三", tip.Title);
        Assert.Equal(["250万", "0", "3.6万"], tip.Rows.Select(r => r.Value));
        Assert.Equal(["推算", null, "不完整"], tip.Rows.Select(r => r.Flag));
        Assert.Contains("2 条记录无法核验", tip.Note);
        var missing = vm.BuildTip(vm.Grid.Days.Single(d => d.Date == new DateOnly(2026, 10, 1)));
        Assert.All(missing.Rows, r => Assert.Equal("无记录", r.Value));
    }
    [Fact] public void LaneSummariesReportBackfillAndPinnedFocus()
    {
        var vm = new MonitorViewModel();
        vm.UpdateHistory(History("claude", D(9, 1000), D(10, 2000)) with { PendingDays = 30, Scope = "ClaudeCN", Source = "stat" }, Today);
        var claude = vm.Lanes.Single(l => l.Id == "claude");
        Assert.Equal("3,000", claude.Total); Assert.StartsWith("回溯中", claude.Status);
        Assert.Equal("读取中", vm.Lanes.Single(l => l.Id == "codex").Status);
        vm.TogglePinnedLane("claude"); Assert.Equal("claude", vm.FocusLane); Assert.True(vm.Lanes.Single(l => l.Id == "claude").Pinned);
        vm.HoverLane = "dsh"; Assert.Equal("dsh", vm.FocusLane);
        vm.HoverLane = null; vm.TogglePinnedLane("claude"); Assert.Null(vm.FocusLane);
    }
}
