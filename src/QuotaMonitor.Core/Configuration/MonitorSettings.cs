namespace QuotaMonitor.Core.Configuration;

public sealed class MonitorSettings
{
    public bool CodexEnabled { get; set; } = true;
    public bool ClaudeEnabled { get; set; } = true;
    public bool DshEnabled { get; set; } = true;
    public bool AntigravityEnabled { get; set; } = true;
    // Presentation only: hiding a Harness never clears its connection or history.
    public bool ShowCodex { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ShowDsh { get; set; } = true;
    public bool ShowAntigravity { get; set; } = true;
    public bool Visible(string id) => id switch { "codex"=>ShowCodex, "claude"=>ShowClaude, "dsh"=>ShowDsh, "antigravity"=>ShowAntigravity, _=>false };
    public double AntigravityGeminiLowPercent { get; set; } = 15;
    public double AntigravityClaudeGptLowPercent { get; set; } = 15;
    public int AntigravityRefreshSeconds { get; set; } = 60;
    public string CodexExecutable { get; set; } = "";
    public string ClaudeUserId { get; set; } = "";
    public decimal QuotaPerUsd { get; set; } = 500000m;
    public string DshSessionRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
    public bool TopCenter { get; set; } = true;
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 12;
    public bool HideInFullscreen { get; set; } = true;
    public bool AutoStart { get; set; }
    public double CodexFiveHourLowPercent { get; set; } = 15;
    public double CodexWeeklyLowPercent { get; set; } = 15;
    public decimal ClaudeLowBalanceUsd { get; set; } = 5m;
    public decimal DshLowBalance { get; set; } = 5m;
    public string DshAlertCurrency { get; set; } = "CNY";
    // Heatmap height scale: "unified" shares one log scale across platforms; "relative" scales each to its own peak.
    public string HeatmapScale { get; set; } = "unified";
    public int CodexRefreshSeconds { get; set; } = 180;
    public int ClaudeRefreshSeconds { get; set; } = 180;
    public int ClaudeUsageSeconds { get; set; } = 300;
    public int DshRefreshSeconds { get; set; } = 90;
    // Verified official price snapshot. Fixed peak rates are an estimate ceiling, not billed cost.
    public string DshPricedModel { get; set; } = "deepseek-flash";
    public string DshPriceBasis { get; set; } = "2026-10-10 官网高峰 USD 单价上限；未应用低峰/节假日折扣，非账户账单。https://api-docs.deepseek.com/quick_start/pricing/";
    public decimal? DshInputPerMillion { get; set; } = 0.30m;
    public decimal? DshCacheReadPerMillion { get; set; } = 0.006m;
    public decimal? DshCacheWritePerMillion { get; set; }
    public decimal? DshOutputPerMillion { get; set; } = 1.20m;
    public string DshPriceCurrency { get; set; } = "USD";
    public bool Enabled(string id) => id switch { "codex"=>CodexEnabled, "claude"=>ClaudeEnabled, "dsh"=>DshEnabled, "antigravity"=>AntigravityEnabled, _=>false };
    public bool DshEstimateEnabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveDshSessionRoot => string.IsNullOrWhiteSpace(DshSessionRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions") : DshSessionRoot;
    public TimeSpan Interval(string id) => TimeSpan.FromSeconds(Math.Clamp(id switch { "codex"=>CodexRefreshSeconds, "claude"=>ClaudeRefreshSeconds, "antigravity"=>AntigravityRefreshSeconds, _=>DshRefreshSeconds }, 30, 3600));
}
