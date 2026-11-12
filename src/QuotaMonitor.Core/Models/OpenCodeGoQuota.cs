namespace QuotaMonitor.Core.Models;

public static class OpenCodeGoQuota
{
    // Official Go allowance sizes: 5h = 20%, week = 50%, month = 100% of the
    // same monthly base. Compare remaining AMOUNTS, then display that window's %.
    // https://opencode.ai/docs/console/go#usage-limits (checked 2026-11-12)
    public static QuotaWindow? MostConstrained(IEnumerable<QuotaWindow> windows) => windows
        .Select(w => (Window: w, Weight: w.Bucket switch { "rolling" => .2, "weekly" => .5, "monthly" => 1d, _ => 0d }, Order: w.Bucket switch { "rolling" => 0, "weekly" => 1, _ => 2 }))
        .Where(p => p.Weight > 0 && double.IsFinite(p.Window.UsedPercent) && p.Window.UsedPercent is >= 0 and <= 100)
        .OrderBy(p => p.Window.RemainingPercent * p.Weight)
        .ThenBy(p => p.Order)
        .Select(p => p.Window).FirstOrDefault();
}
