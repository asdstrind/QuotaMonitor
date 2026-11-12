using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QuotaMonitor.Core.Models;

namespace QuotaMonitor.Core.Adapters;

// Console's daily export is a replaceable, observed contract. No identity leaves this parser.
public static class OpenCodeGoExport
{
    public static UsageHistory Parse(string csv, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(csv))return History([],fetchedAt,false);
        var records = Records(csv).GetEnumerator();
        if (!records.MoveNext()) return History([], fetchedAt, false);
        var headers = records.Current.Select((s, i) => (Name: s.Trim().TrimStart('\uFEFF'), Index: i)).ToArray();
        if (headers.Select(h => h.Name).Distinct(StringComparer.Ordinal).Count() != headers.Length)
            throw new ProviderException("OpenCode Go CSV 字段重复。");
        var columns = headers.ToDictionary(h => h.Name, h => h.Index, StringComparer.Ordinal);
        if (!columns.ContainsKey("day") || !columns.ContainsKey("provider"))
            throw new ProviderException("OpenCode Go CSV 缺少日期或 Provider 字段。");
        string? Field(string[] row, string name) => columns.TryGetValue(name, out var i) && i < row.Length ? row[i] : null;
        long? Count(string[] row, string name) => long.TryParse(Field(row, name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
        var today = DateOnly.FromDateTime(fetchedAt.UtcDateTime);
        var days = new Dictionary<DateOnly, List<(long? Tokens, long? Requests, long? Cache, bool Partial)>>();
        var seen = new Dictionary<string, (DateOnly Day, string Fingerprint, int Index)>();
        bool partial = false;
        while (records.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            var row = records.Current;
            if (row.All(string.IsNullOrEmpty)) continue;
            // Exact billing provider, never a model-name heuristic.
            if (Field(row, "provider") != "opencode-go") continue;
            if (!DateOnly.TryParseExact(Field(row, "day"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day > today || day < today.AddDays(-90))
            { partial = true; continue; }
            if (!days.TryGetValue(day, out var list)) days[day] = list = [];
            var input = Count(row, "input_tokens"); var output = Count(row, "output_tokens");
            var read = Count(row, "cache_read_tokens"); var write5 = Count(row, "cache_write_5m_tokens"); var write1 = Count(row, "cache_write_1h_tokens");
            var tokens = Sum([input, output, read, write5, write1]);
            var requests = Count(row, "requests");
            var validIdentity = !string.IsNullOrEmpty(Field(row, "user_type")) && !string.IsNullOrEmpty(Field(row, "user_id")) && !string.IsNullOrEmpty(Field(row, "model"));
            var bad = !validIdentity || row.Length != headers.Length || tokens is null || requests is null;
            // Identity strings are consumed only to form an ephemeral hash; user_name is ignored.
            var identity = Hash(string.Join("\0", new[] { day.ToString("yyyy-MM-dd"), Field(row, "user_type"), Field(row, "user_id"), "opencode-go", Field(row, "model") }));
            var fingerprint = string.Join("|", new[] { input, output, read, write5, write1, requests }.Select(n => n?.ToString(CultureInfo.InvariantCulture) ?? "?"));
            if (seen.TryGetValue(identity, out var prior))
            {
                if (prior.Fingerprint == fingerprint) continue;
                // Contradictory duplicate keys cannot establish either amount.
                days[prior.Day][prior.Index] = (null, null, null, true); partial = true; continue;
            }
            seen[identity] = (day, fingerprint, list.Count);
            list.Add((bad ? null : tokens, requests, read, bad)); partial |= bad;
        }
        var result = days.OrderBy(p => p.Key).Select(p =>
        {
            var tokens = Sum(p.Value.Select(r => r.Tokens)); var requests = Sum(p.Value.Select(r => r.Requests));
            var bad = p.Value.Any(r => r.Partial) || tokens is null || requests is null;
            return new UsageDay(p.Key, tokens, requests, null, bad ? UsageQuality.Partial : UsageQuality.Exact, fetchedAt, Sum(p.Value.Select(r => r.Cache)), p.Value.Count(r => r.Partial));
        }).ToArray();
        return History(result, fetchedAt, partial || result.Any(d => d.Quality == UsageQuality.Partial));
    }

    private static UsageHistory History(IReadOnlyList<UsageDay> days, DateTimeOffset at, bool partial) => new()
    {
        ProviderId = "opencode-go", Days = days, CoverageStart = days.Count > 0 ? days.Min(d => d.Date) : null,
        Source = "OpenCode Console · 90d CSV", Scope = "provider=opencode-go · UTC 日",
        Note = "仅服务端返回的日期；缺失日期未知。UTC 日保留原日期，不转换为本地日。近期汇总可能延迟。",
        Error = partial ? "服务端历史包含无法核实的部分；未补零。" : null, UpdatedAt = at
    };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static long? Sum(IEnumerable<long?> values)
    {
        long total = 0;
        try { foreach (var value in values) { if (value is null) return null; total = checked(total + value.Value); } return total; }
        catch (OverflowException) { return null; }
    }
    // RFC 4180 quoting, embedded CR/LF and BOM. Reject broken quotes instead of shifting columns.
    private static IEnumerable<string[]> Records(string text)
    {
        if (text.Length > 16 * 1024 * 1024) throw new ProviderException("OpenCode Go CSV 超出大小限制。");
        var row = new List<string>(); var field = new StringBuilder(); bool quoted = false, closed = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i == 0 && c == '\uFEFF') continue;
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; closed = true; } }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0 && !closed) quoted = true;
            else if (c == ',' || c == '\r' || c == '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c != ',') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; yield return row.ToArray(); row.Clear(); }
            }
            else if (closed || c == '"') throw new ProviderException("OpenCode Go CSV 引号格式变化。");
            else field.Append(c);
            if (field.Length > 1024 * 1024 || row.Count > 256) throw new ProviderException("OpenCode Go CSV 字段超出限制。");
        }
        if (quoted) throw new ProviderException("OpenCode Go CSV 不完整。");
        if (field.Length > 0 || row.Count > 0 || closed) { row.Add(field.ToString()); yield return row.ToArray(); }
    }
}
