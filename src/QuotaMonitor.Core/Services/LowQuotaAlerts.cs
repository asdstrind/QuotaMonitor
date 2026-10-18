using System.Globalization;
using System.Numerics;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;

namespace QuotaMonitor.Core.Services;

public sealed record LowQuotaAlert(string Key, string Label, string Color, string Description, string Value = "", string? SecondLabel = null, string? SecondValue = null);

public static class LowQuotaAlerts
{
    // Keep the independent threshold decisions for diagnostics; presentation has one slot per Harness.
    public static IReadOnlyList<LowQuotaAlert> Minimal(IReadOnlyList<LowQuotaAlert> alerts)
    {
        var result = alerts.Where(a => !a.Key.StartsWith("ag-", StringComparison.Ordinal) && (a.Key != "codex-5h" || !alerts.Any(b => b.Key == "codex-week"))).ToList();
        var ag = alerts.Where(a => a.Key.StartsWith("ag-", StringComparison.Ordinal)).ToArray();
        if (ag.Length > 0) result.Add(ag[0] with { Key = "antigravity", SecondLabel = ag.Length > 1 ? ag[1].Label : null, SecondValue = ag.Length > 1 ? ag[1].Value : null });
        return result.OrderBy(a => a.Key.StartsWith("codex", StringComparison.Ordinal) ? 0 : a.Key == "claude" ? 1 : a.Key == "dsh" ? 2 : 3).ToArray();
    }
    public static IReadOnlyList<LowQuotaAlert> Evaluate(IEnumerable<ProviderSnapshot> snapshots, MonitorSettings settings, DateTimeOffset now)
    {
        var alerts = new List<LowQuotaAlert>();
        foreach (var s in snapshots)
        {
            if (!settings.Visible(s.ProviderId) || !settings.Enabled(s.ProviderId) || s.ConnectionStatus == ConnectionStatus.Disabled || s.NeedsSetup) continue;
            if (s.ProviderId == "dsh")
            {
                if (settings.DshLowBalance <= 0 || s.AccountBalance?.At(now) != DshBalanceState.Ready ||
                    s.AccountBalance.Balance is not { } balance || balance.ObservedAt > now.AddSeconds(30)) continue;
                var wallets = balance.NormalWallets.Where(w => w.Currency == settings.DshAlertCurrency).ToArray();
                if (wallets.Length > 0 && Below(wallets, settings.DshLowBalance))
                    alerts.Add(new("dsh", "D", "#4A8AF0", $"DSH 充值余额低于 {settings.DshAlertCurrency} {settings.DshLowBalance}", SumDisplay(wallets, settings.DshAlertCurrency)));
                continue;
            }
            if (s.ConnectionStatus is not (ConnectionStatus.Ready or ConnectionStatus.Partial) || s.IsEstimated ||
                s.LastUpdated is not { } at || now - at > TimeSpan.FromMinutes(s.ProviderId == "antigravity" ? 3 : 7) || at > now.AddSeconds(30)) continue;
            if (s.ProviderId == "antigravity")
            {
                foreach (var (id, label, threshold) in new[] { ("gemini-weekly", "Gem", settings.AntigravityGeminiLowPercent), ("3p-weekly", "C/G", settings.AntigravityClaudeGptLowPercent) })
                {
                    var w = s.Windows.FirstOrDefault(w => w.Bucket == id && w.DurationMinutes == 10080);
                    if (!double.IsFinite(threshold) || threshold <= 0 || threshold > 100 || w is null || !double.IsFinite(w.UsedPercent) || w.UsedPercent is < 0 or > 100 || w.ResetAt is { } reset && reset <= now || w.RemainingPercent >= threshold) continue;
                    alerts.Add(new("ag-" + id, label, "#B59AF7", $"Antigravity {w.Name} Weekly 剩余 {w.RemainingPercent:0.#}% · 低于 {threshold:0.#}%", w.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"));
                }
                continue;
            }
            if (s.ProviderId == "claude" && settings.ClaudeLowBalanceUsd > 0 && s.Currency == "USD" && s.RemainingBalance is { } amount && amount < settings.ClaudeLowBalanceUsd)
                alerts.Add(new("claude", "C", "#E2672F", $"ClaudeCN 余额低于 USD {settings.ClaudeLowBalanceUsd}", "$" + amount.ToString("0.00", CultureInfo.InvariantCulture)));
            if (s.ProviderId != "codex" || s.Windows.Count == 0) continue;
            var bucket = s.Windows.Any(w => w.Bucket == "codex") ? "codex" : s.Windows[0].Bucket;
            foreach (var (duration, threshold, key, label) in new[] {
                (300L, settings.CodexFiveHourLowPercent, "codex-5h", "5h"),
                (10080L, settings.CodexWeeklyLowPercent, "codex-week", "W") })
            {
                if (!double.IsFinite(threshold) || threshold <= 0 || threshold > 100) continue;
                var window = s.Windows.FirstOrDefault(w => w.Bucket == bucket && w.DurationMinutes == duration);
                if (window is null || !double.IsFinite(window.UsedPercent) || window.UsedPercent is < 0 or > 100 ||
                    window.ResetAt is { } reset && reset <= now || window.RemainingPercent >= threshold) continue;
                alerts.Add(new(key, label, "#19A67A", $"Codex {(duration == 300 ? "5 小时" : "周")}剩余额度 {window.RemainingPercent:0.#}% · 低于 {threshold:0.#}%", (duration == 300 ? "5h " : "7d ") + window.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"));
            }
        }
        return alerts;
    }

    private static string SumDisplay(IReadOnlyList<WalletBalance> wallets, string currency)
    {
        var numbers = wallets.Select(w => { Parse(w.Balance, out var n); return n; }).ToArray();
        var scale = numbers.Max(n => n.Scale);
        var digits = numbers.Aggregate(BigInteger.Zero, (sum, n) => sum + n.Digits * BigInteger.Pow(10, scale - n.Scale));
        return DshBalancePresentation.Wallet(new(currency, digits.ToString(CultureInfo.InvariantCulture) + "e" + (-scale).ToString(CultureInfo.InvariantCulture)));
    }

    // Compare the original wallet strings exactly, including scientific notation; no display rounding or FX conversion.
    private static bool Below(IEnumerable<WalletBalance> wallets, decimal threshold)
    {
        var numbers = new List<(BigInteger Digits, int Scale)>();
        foreach (var wallet in wallets)
        {
            if (!Parse(wallet.Balance, out var value)) return false;
            numbers.Add(value);
        }
        if (!Parse(threshold.ToString(CultureInfo.InvariantCulture), out var limit)) return false;
        var scale = Math.Max(limit.Scale, numbers.Max(n => n.Scale));
        var sum = numbers.Aggregate(BigInteger.Zero, (total, n) => total + n.Digits * BigInteger.Pow(10, scale - n.Scale));
        return sum < limit.Digits * BigInteger.Pow(10, scale - limit.Scale);
    }
    private static bool Parse(string text, out (BigInteger Digits, int Scale) value)
    {
        value = default;
        if (!DshBalancePresentation.ValidAmount(text)) return false;
        var parts = text.Split(['e', 'E']);
        var exponent = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) || Math.Abs((long)exponent) > 1000)) return false;
        var dot = parts[0].IndexOf('.');
        value = (BigInteger.Parse(parts[0].Replace(".", ""), CultureInfo.InvariantCulture), (dot < 0 ? 0 : parts[0].Length - dot - 1) - exponent);
        return true;
    }
}
