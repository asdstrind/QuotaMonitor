using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Core.Adapters;
public sealed class ClaudeCNAdapter(HttpClient http, MonitorSettings settings, ISecretStore secrets, TimeProvider? clock = null) : IProviderAdapter, IUsageHistorySource
{
    public string Id => "claude";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private ProviderSnapshot? previous;
    private DateOnly? usageDay;
    private string? credentialFingerprint;
    private const string Origin = "https://claudecn.top";
    public static decimal ToUsd(decimal quota, decimal ratio) => ratio > 0 ? quota/ratio : throw new ArgumentOutOfRangeException(nameof(ratio));
    public async Task<ProviderSnapshot> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var token=secrets.Read();
        if(string.IsNullOrWhiteSpace(token) || !long.TryParse(settings.ClaudeUserId, NumberStyles.None, CultureInfo.InvariantCulture,out var id) || id<=0)
            return ProviderSnapshot.Empty(Id,"Claude",MetricType.Balance) with { ConnectionStatus=ConnectionStatus.Unavailable, NeedsSetup=true, ErrorMessage="请在设置中输入重新生成的 System Access Token 和数字 User ID。" };
        return await QueryAsync(token, id, force, cancellationToken);
    }
    public async Task<ProviderSnapshot> QueryAsync(string token, long userId, bool force, CancellationToken ct)
    {
        var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token+"|"+userId.ToString(CultureInfo.InvariantCulture)+"|"+settings.QuotaPerUsd.ToString(CultureInfo.InvariantCulture))));
        if(fingerprint!=credentialFingerprint){previous=null;usageDay=null;credentialFingerprint=fingerprint;}
        var now=time.GetUtcNow(); var local=TimeZoneInfo.ConvertTime(now,TimeZoneInfo.Local);
        var day=DateOnly.FromDateTime(local.DateTime);
        using var account=await GetAsync("/api/user/self",token,userId,ct);
        var data=RequireData(account.RootElement);
        var quota=data.Number("quota") ?? throw new ProviderException("账户接口缺少 quota，不能计算余额。");
        var used=data.Number("used_quota"); var billing=data.Text("billing_preference");
        var result=ProviderSnapshot.Empty(Id,"Claude",MetricType.Balance) with { ConnectionStatus=ConnectionStatus.Ready, RemainingBalance=ToUsd(quota,settings.QuotaPerUsd), TotalCost=used.HasValue?ToUsd(used.Value,settings.QuotaPerUsd):null, Currency="USD", LastUpdated=now, DataSource="ClaudeCN /api/user/self + /api/log/self/stat + /api/log/self", BillingPreference=billing, Note="使用额度按配置比例换算；不等同于钱包实际扣款。结算偏好："+(billing??"未知") };
        if(!force && previous?.UsageUpdated is { } updated && now-updated<TimeSpan.FromSeconds(settings.ClaudeUsageSeconds) && usageDay==day)
            return previous=result with { TodayCost=previous.TodayCost, TodayTokens=previous.TodayTokens, TodayRequests=previous.TodayRequests, UsageUpdated=updated, RecentRequests=previous.RecentRequests };
        var midnight=new DateTime(local.Year,local.Month,local.Day,0,0,0,DateTimeKind.Unspecified);
        var start=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight,TimeZoneInfo.Local)).ToUnixTimeSeconds();
        var errors=new List<string>();
        try {
            using var stats=await GetAsync($"/api/log/self/stat?type=2&start_timestamp={start}&end_timestamp={now.ToUnixTimeSeconds()}",token,userId,ct);
            var s=RequireData(stats.RootElement);
            result=result with { TodayCost=s.Number("quota") is { } q?ToUsd(q,settings.QuotaPerUsd):null, TodayRequests=s.Count("total_requests"), TodayTokens=s.Count("total_tokens"), UsageUpdated=now };
            usageDay=day;
        } catch(ProviderException ex) { errors.Add("今日统计："+ex.Message); }
        catch(HttpRequestException) { errors.Add("今日统计：网络连接失败。"); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { errors.Add("今日统计：请求超时。"); }
        try {
            using var logs=await GetAsync("/api/log/self?p=1&page_size=5&type=2",token,userId,ct);
            result=result with { RecentRequests=ParseLogs(RequireData(logs.RootElement),settings.QuotaPerUsd) };
        } catch(ProviderException ex) { errors.Add("最近请求："+ex.Message); }
        catch(HttpRequestException) { errors.Add("最近请求：网络连接失败。"); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { errors.Add("最近请求：请求超时。"); }
        if(errors.Count>0)result=result with { ConnectionStatus=ConnectionStatus.Partial, ErrorMessage=string.Join(" ",errors) };
        return previous=result;
    }
    private async Task<JsonDocument> GetAsync(string route,string token,long userId,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,Origin+route);
        req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        req.Headers.Add("New-Api-User",userId.ToString(CultureInfo.InvariantCulture));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        if(!response.IsSuccessStatusCode) throw new ProviderException($"HTTP {(int)response.StatusCode}（检查新 Token、User ID 和接口权限）");
        JsonDocument doc;
        try { await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token); doc=await JsonDocument.ParseAsync(stream,cancellationToken:timeout.Token); }
        catch(JsonException) { throw new ProviderException($"HTTP {(int)response.StatusCode}：响应不是有效 JSON。"); }
        if(doc.RootElement.Field("success") is not { ValueKind:JsonValueKind.True }) {doc.Dispose(); throw new ProviderException($"HTTP {(int)response.StatusCode}：success 不为 true（响应正文已隐藏）。");}
        return doc;
    }
    public static JsonElement RequireData(JsonElement root) => root.Field("data") ?? throw new ProviderException("HTTP 200：缺少 data 字段。");
    // Daily history uses the same consumption statistics as "today", one local calendar day per
    // request. Finished days are fetched once; a day fetched before midnight is re-read once later.
    public const int HistoryBatch = 24;
    private const string HistorySource = "ClaudeCN /api/log/self/stat · type=2 · 按本地自然日";
    private const string HistoryScope = "ClaudeCN 账户（服务端消费日志）";
    public async Task<UsageHistory> ReadHistoryAsync(UsageHistory? previous, HistoryRequest request, CancellationToken ct)
    {
        var token=secrets.Read();
        if(string.IsNullOrWhiteSpace(token) || !long.TryParse(settings.ClaudeUserId, NumberStyles.None, CultureInfo.InvariantCulture,out var id) || id<=0)
            return UsageHistory.Empty(Id) with { Error="未配置 ClaudeCN Token 与 User ID，历史用量不可用。", Source=HistorySource, Scope=HistoryScope, UpdatedAt=time.GetUtcNow() };
        var account=$"claudecn:{id.ToString(CultureInfo.InvariantCulture)}:{settings.QuotaPerUsd.ToString(CultureInfo.InvariantCulture)}";
        var known=previous?.Account==account ? previous.Days.Where(d=>d.Date>=request.WindowStart&&d.Date<=request.Today).ToDictionary(d=>d.Date) : new Dictionary<DateOnly,UsageDay>();
        var now=time.GetUtcNow();
        var wanted=new List<DateOnly>();
        if(!known.TryGetValue(request.Today,out var current) || request.Force || current.FetchedAt is not { } at || now-at>=TimeSpan.FromMinutes(4)) wanted.Add(request.Today);
        for(var day=request.Today.AddDays(-1);day>=request.WindowStart;day=day.AddDays(-1))
            if(!known.TryGetValue(day,out var have) || have.FetchedAt is not { } fetchedAt || fetchedAt<LocalMidnight(day.AddDays(1))) wanted.Add(day);
        string? error=null; var fetched=0;
        foreach(var day in wanted.Take(HistoryBatch))
        {
            try { known[day]=await FetchDayAsync(day,token,id,now,ct); fetched++; }
            catch(ProviderException ex) { error="历史统计："+ex.Message; break; }
            catch(HttpRequestException) { error="历史统计：网络连接失败。"; break; }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested) { error="历史统计：请求超时。"; break; }
        }
        var days=known.Values.OrderBy(d=>d.Date).ToArray();
        return new UsageHistory { ProviderId=Id, Days=days, CoverageStart=days.FirstOrDefault()?.Date, Account=account, Source=HistorySource, Scope=HistoryScope, CostCurrency="USD", PendingDays=wanted.Count-fetched, Error=error, UpdatedAt=now,
            Note="按本地自然日查询 type=2 消费日志；金额为 quota 按比例换算的使用费用，订阅优先结算时不等同于钱包扣款。" };
    }
    private DateTimeOffset LocalMidnight(DateOnly day) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue),time.LocalTimeZone),TimeSpan.Zero);
    private async Task<UsageDay> FetchDayAsync(DateOnly day,string token,long userId,DateTimeOffset now,CancellationToken ct)
    {
        var start=LocalMidnight(day); var end=LocalMidnight(day.AddDays(1)).AddSeconds(-1);
        if(end>now) end=now;
        using var stats=await GetAsync($"/api/log/self/stat?type=2&start_timestamp={start.ToUnixTimeSeconds()}&end_timestamp={end.ToUnixTimeSeconds()}",token,userId,ct);
        var s=RequireData(stats.RootElement);
        var quota=s.Number("quota"); var tokens=s.Count("total_tokens"); var requests=s.Count("total_requests");
        // A missing field stays unknown; it is never stored as zero.
        if(quota is null && tokens is null && requests is null) throw new ProviderException("统计接口未返回 quota / total_tokens / total_requests。");
        return new UsageDay(day,tokens,requests,quota is { } q?ToUsd(q,settings.QuotaPerUsd):null,UsageQuality.Exact,now);
    }
    public static IReadOnlyList<RequestLog> ParseLogs(JsonElement data,decimal ratio)
    {
        var list=data.ValueKind==JsonValueKind.Array?data:data.Field("items") ?? data.Field("logs");
        if(list is not { ValueKind:JsonValueKind.Array } arr) throw new ProviderException("日志结构不受支持，未将未知结构当作空日志。");
        return arr.EnumerateArray().Where(e=>e.Count("type")==2).Select(e=>new RequestLog(e.Text("model_name")??"未知模型", e.Count("prompt_tokens"),e.Count("completion_tokens"),e.Number("quota") is {} q?ToUsd(q,ratio):null,JsonFields.Unix(e.Count("created_at")),ParseBilling(e))).Take(5).ToArray();
    }
    private static string? ParseBilling(JsonElement e)
    {
        if(e.Text("other") is not {} text)return null;
        try {using var doc=JsonDocument.Parse(text);return doc.RootElement.Text("billing_source");}catch(JsonException){return null;}
    }
}
public sealed class ProviderException(string message) : Exception(message);
