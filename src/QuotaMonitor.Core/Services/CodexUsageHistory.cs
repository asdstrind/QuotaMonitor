using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaMonitor.Core.Models;
using ZstdSharp;

namespace QuotaMonitor.Core.Services;

/// <summary>
/// Read-only daily token history from local Codex rollouts (plain and zstd-compressed JSONL).
/// Only token accounting events are parsed; conversation content is never retained.
/// </summary>
public sealed class CodexUsageHistory(TimeZoneInfo? zone = null)
{
    private const long MaxDecodedBytes = 512L * 1024 * 1024;
    private const int MaxLineBytes = 64 * 1024 * 1024;
    private static readonly byte[] RecordMarker = "\"token_usage_record\""u8.ToArray();
    private static readonly byte[] CountMarker = "\"token_count\""u8.ToArray();
    private readonly TimeZoneInfo zone = zone ?? TimeZoneInfo.Local;
    private readonly Dictionary<string, FileState> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    private readonly record struct UsageEvent(ulong Key, DateOnly Day, long Tokens, long Cached, bool Estimated);
    private sealed class FileState
    {
        public long Length;
        public DateTime Modified;
        public long Offset;
        public bool RecordMode;
        public long? LastCumulative;
        public DateOnly? LastDay;
        public DateOnly? NameDay;
        public bool Unreadable;
        public bool Legacy;
        public List<UsageEvent> Events = [];
        public HashSet<ulong> Keys = [];
        public Dictionary<DateOnly, int> Invalid = [];
        public int InvalidUnattributed;
    }

    public static string DefaultHome() => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static IEnumerable<string> RolloutFiles(string home)
    {
        foreach (var folder in new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "rollout-*", SearchOption.AllDirectories))
                if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase)) yield return file;
        }
    }

    public UsageHistory Read(string home, DateOnly today, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(home, "sessions")) && !Directory.Exists(Path.Combine(home, "archived_sessions")))
            return UsageHistory.Empty("codex") with { Error = "未找到 Codex 会话目录；历史用量不可用。", Source = Source, Scope = Scope, UpdatedAt = DateTimeOffset.UtcNow };
        lock (gate)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in RolloutFiles(home))
            {
                ct.ThrowIfCancellationRequested();
                present.Add(file);
                Refresh(file, ct);
            }
            foreach (var gone in files.Keys.Where(k => !present.Contains(k)).ToArray()) files.Remove(gone);
            return Aggregate(today);
        }
    }

    private const string Source = "Codex 本机 rollout · token_usage_record / token_count";
    private const string Scope = "本机 Codex 会话（含子代理与已归档会话）";

    private void Refresh(string file, CancellationToken ct)
    {
        FileInfo info;
        try { info = new FileInfo(file); if (!info.Exists) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var compressed = file.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
        files.TryGetValue(file, out var state);
        if (state is not null && state.Length == info.Length && state.Modified == info.LastWriteTimeUtc) return;
        // Plain rollouts are append-only: continue from the last complete line. Anything else restarts.
        var append = state is not null && !compressed && !state.Unreadable && info.Length > state.Length && info.Length >= state.Offset;
        var target = append ? state! : new FileState { NameDay = NameDay(file) };
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (compressed)
            {
                using var decoded = new DecompressionStream(stream);
                ScanLines(new BoundedStream(decoded, MaxDecodedBytes), target, true, ct);
            }
            else
            {
                stream.Seek(target.Offset, SeekOrigin.Begin);
                target.Offset += ScanLines(stream, target, false, ct);
            }
            target.Length = info.Length; target.Modified = info.LastWriteTimeUtc; target.Unreadable = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdException)
        {
            target = new FileState { NameDay = NameDay(file), Unreadable = true, Length = info.Length, Modified = info.LastWriteTimeUtc };
        }
        files[file] = target;
    }

    // Returns bytes consumed through the last complete line. A compressed file is complete, so its
    // final unterminated line is processed too; a plain file may still be mid-write.
    private long ScanLines(Stream input, FileState state, bool complete, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int filled = 0; long consumed = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (filled == buffer.Length)
                {
                    if (buffer.Length >= MaxLineBytes) throw new InvalidDataException("Rollout line exceeds bounded reader");
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer); buffer = larger;
                }
                var read = input.Read(buffer, filled, buffer.Length - filled);
                if (read == 0) break;
                filled += read;
                var start = 0;
                while (true)
                {
                    var newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start);
                    if (newline < 0) break;
                    Line(buffer.AsSpan(start, newline - start), state);
                    consumed += newline - start + 1; start = newline + 1;
                }
                if (start > 0) { Buffer.BlockCopy(buffer, start, buffer, 0, filled - start); filled -= start; }
            }
            if (complete && filled > 0) { Line(buffer.AsSpan(0, filled), state); consumed += filled; }
            return consumed;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void Line(ReadOnlySpan<byte> line, FileState state)
    {
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
        if (line.IsEmpty) return;
        // Structural markers sit in the first fields of an event; payload text is never inspected.
        var head = line.Length <= 4096 ? line : line[..256];
        var isRecord = head.IndexOf(RecordMarker) >= 0;
        var isCount = !isRecord && head.IndexOf(CountMarker) >= 0;
        if (!isRecord && !isCount) return;
        if (isCount && state.RecordMode) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line.ToArray()); }
        catch (JsonException) { Invalid(state, null); return; }
        using (doc)
        {
            var root = doc.RootElement;
            var type = root.Text("type");
            var payload = root.Field("payload");
            if (type == "token_usage_record" && payload is { } record) { Record(root, record, state); return; }
            if (type == "event_msg" && payload is { } message && message.Text("type") == "token_count" && !state.RecordMode) Legacy(root, message, state);
        }
    }

    private void Record(JsonElement root, JsonElement payload, FileState state)
    {
        state.RecordMode = true;
        var day = Day(root);
        var usage = payload.Field("usage");
        var input = usage?.Count("input_tokens"); var output = usage?.Count("output_tokens");
        if (day is null || input is null || output is null) { Invalid(state, day); return; }
        long total;
        try { total = checked(input.Value + output.Value); } catch (OverflowException) { Invalid(state, day); return; }
        if (usage!.Value.Count("total_tokens") is { } declared && declared != total) { Invalid(state, day); return; }
        var id = payload.Text("response_id") ?? $"{root.Text("timestamp")}|{payload.Text("turn_id")}|{total}";
        Add(state, new(Hash("R|" + id), day.Value, total, Math.Min(usage.Value.Count("cached_input_tokens") ?? 0, input.Value), false));
    }

    // Older CLI builds only emit cumulative counters. Each change of the running total is one billed
    // response; re-emissions repeat the total and are skipped. Anything inconsistent is flagged.
    private void Legacy(JsonElement root, JsonElement payload, FileState state)
    {
        if (payload.Field("info") is not { ValueKind: JsonValueKind.Object } info) return;
        var day = Day(root);
        var cumulative = info.Field("total_token_usage")?.Count("total_tokens");
        var lastUsage = info.Field("last_token_usage");
        var last = lastUsage?.Count("total_tokens");
        if (day is null || cumulative is null || last is null) { Invalid(state, day); return; }
        if (state.LastCumulative == cumulative) return;
        var previous = state.LastCumulative; state.LastCumulative = cumulative;
        long tokens = last.Value; var estimated = false;
        if (previous is null) estimated = cumulative != last;
        else if (cumulative < previous) estimated = true;
        else if (cumulative - previous != last) { tokens = cumulative.Value - previous.Value; estimated = true; }
        state.Legacy = true;
        var cached = Math.Min(lastUsage!.Value.Count("cached_input_tokens") ?? 0, tokens);
        Add(state, new(Hash($"L|{root.Text("timestamp")}|{cumulative}|{last}"), day.Value, tokens, cached, estimated));
    }

    private static void Add(FileState state, UsageEvent value)
    {
        if (!state.Keys.Add(value.Key)) return;
        state.Events.Add(value); state.LastDay = value.Day;
    }

    private static void Invalid(FileState state, DateOnly? day)
    {
        var at = day ?? state.LastDay;
        if (at is { } d) state.Invalid[d] = state.Invalid.GetValueOrDefault(d) + 1; else state.InvalidUnattributed++;
    }

    private DateOnly? Day(JsonElement root)
    {
        if (root.Text("timestamp") is not { } text || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
    }

    private UsageHistory Aggregate(DateOnly today)
    {
        // Copies (a rollout briefly existing plain and compressed) are removed by event key. Session
        // ids are not unique per file: a continued session writes a new segment under the same id.
        var chosen = files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => f.Value).ToArray();
        var seen = new HashSet<ulong>();
        var days = new Dictionary<DateOnly, (long Tokens, long Requests, long Cached, bool Estimated)>();
        var partial = new Dictionary<DateOnly, int>();
        int unattributed = 0, unreadable = 0;
        foreach (var state in chosen)
        {
            foreach (var e in state.Events)
            {
                if (!seen.Add(e.Key)) continue;
                var d = days.GetValueOrDefault(e.Day);
                days[e.Day] = (d.Tokens + e.Tokens, d.Requests + 1, d.Cached + e.Cached, d.Estimated || e.Estimated);
            }
            foreach (var (day, count) in state.Invalid) partial[day] = partial.GetValueOrDefault(day) + count;
            unattributed += state.InvalidUnattributed;
            if (state.Unreadable) { unreadable++; if (state.NameDay is { } nd) partial[nd] = partial.GetValueOrDefault(nd) + 1; else unattributed++; }
        }
        var legacy = chosen.Any(s => s.Legacy);
        if (days.Count == 0 && partial.Count == 0)
            return UsageHistory.Empty("codex") with { Source = Source, Scope = Scope, UpdatedAt = DateTimeOffset.UtcNow, Error = unattributed + unreadable > 0 ? $"{unreadable} 个会话文件无法读取；没有可验证的用量记录。" : "没有找到包含用量记录的 Codex 会话。" };
        var start = days.Keys.Concat(partial.Keys).Min();
        var list = new List<UsageDay>();
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            var excluded = partial.GetValueOrDefault(day);
            var q = excluded > 0 ? UsageQuality.Partial : days.TryGetValue(day, out var v) && v.Estimated ? UsageQuality.Estimated : UsageQuality.Exact;
            list.Add(days.TryGetValue(day, out var value) ? new UsageDay(day, value.Tokens, value.Requests, null, q, null, value.Cached, excluded) : new UsageDay(day, 0, 0, null, q, null, 0, excluded));
        }
        var notes = new List<string>();
        if (legacy) notes.Add("早期 CLI 版本仅记录累计计数，相应日期由累计值的逐次增量还原，增量与单次用量不一致处标为推算。");
        if (unreadable > 0) notes.Add($"{unreadable} 个会话文件无法读取，对应日期标为不完整。");
        if (unattributed > 0) notes.Add($"另有 {unattributed} 条用量记录无法解析且无法确定日期，已排除。");
        return new UsageHistory { ProviderId = "codex", Days = list, CoverageStart = start, Source = Source, Scope = Scope, Note = notes.Count > 0 ? string.Join(" ", notes) : null, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private static DateOnly? NameDay(string file)
    {
        var m = Regex.Match(Path.GetFileName(file), @"^rollout-(\d{4})-(\d{2})-(\d{2})T");
        return m.Success && DateOnly.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static ulong Hash(string text)
    {
        // FNV-1a 64-bit: stable within a process, used only for de-duplication keys.
        ulong h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text)) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private long total;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count); total += n;
            if (total > limit) throw new InvalidDataException("Decoded rollout exceeds bounded reader");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
