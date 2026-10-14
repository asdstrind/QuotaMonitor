using System.Text.Json;
namespace QuotaMonitor.Core.Services;
public static class JsonFields
{
    public static JsonElement? Field(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name,out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
    public static string? Text(this JsonElement e, string name) => e.Field(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static decimal? Number(this JsonElement e, string name) => e.Field(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDecimal(out var n) ? n : null;
    public static long? Count(this JsonElement e, string name) => e.Field(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) && n>=0 ? n : null;
    public static DateTimeOffset? Unix(long? n) { try { return n.HasValue ? DateTimeOffset.FromUnixTimeSeconds(n.Value) : null; } catch(ArgumentOutOfRangeException) {return null;} }
}
