using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Security;
using QuotaMonitor.ViewModels;
using QuotaMonitor.Views;

// Offline showcase only. Never instantiate QuotaMonitor.App or any adapter.
// Every number is invented; Settings uses an isolated empty store and a random,
// nonexistent credential target. It renders the unchanged production WPF views
// into bitmaps. No desktop capture, no network, no account or credential read.
public static class Showcase
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr Bottom = new(1);
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Arguments: repository root, output directory");
        var root = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var x = XDocument.Load(Path.Combine(root, "src/QuotaMonitor/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", x.Root!.Element(ns + "Application.Resources")!.Elements());
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        app.Startup += async (_, _) =>
        {
            try { await Generate(root, output); app.Shutdown(0); }
            catch (Exception e) { File.WriteAllText(Path.Combine(output, "error.txt"), e.ToString()); app.Shutdown(1); }
        };
        app.Run();
    }

    static async Task Generate(string root, string output)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var settings = new MonitorSettings { TopCenter = true, Left = 100, Top = 12, HideInFullscreen = false, AutoStart = false, CodexExecutable = "codex", DshSessionRoot = "" };
        var vm = new MonitorViewModel();
        var bases = new Dictionary<string, long> { ["codex"] = 15000, ["dsh"] = 26000, ["antigravity"] = 11000, ["opencode-go"] = 19000 };
        // Invented deterministic history: weekday rhythm, slow waves, occasional peaks and zero days.
        foreach (var (id, lane) in UsageGrid.Order.Select((id, lane) => (id, lane)))
        {
            var days = Enumerable.Range(0, 182).Select(i =>
            {
                var date = today.AddDays(i - 181);
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var wave = 0.55 + ((i * 7 + lane * 13) % 18) / 17d;
                var peak = (i * 5 + lane * 7) % 31 == lane + 5 ? 2.4 : 1d;
                var tokens = (long)Math.Round(bases[id] * (weekend ? 0.55 : 1d) * wave * peak * (1 + .06 * Math.Sin(i * .6 + lane * 2)));
                if ((i * 11 + lane * 17) % 41 == lane) tokens = 0;
                if (i == 181) tokens = Math.Max(tokens, bases[id] * 3 / 2);
                return new UsageDay(date, tokens, tokens / 90 + 4, 0.0000004m * tokens, UsageQuality.Exact, now, null);
            }).ToArray();
            vm.UpdateHistory(new UsageHistory
            {
                ProviderId = id, Days = days, CoverageStart = today.AddDays(-181), UpdatedAt = now,
                Source = "Synthetic showcase fixture", Scope = "Invented daily totals",
                CostCurrency = id == "dsh" ? "CNY" : "USD"
            }, today);
        }
        foreach (var id in UsageGrid.Order) vm.Providers.Add(new ProviderViewModel(new ProviderSnapshot { ProviderId = id, DisplayName = MonitorViewModel.NameOf(id), MetricType = id == "dsh" ? MetricType.Balance : MetricType.Quota, ConnectionStatus = ConnectionStatus.Ready }));
        ResetProviders(vm, now, alerts: false);
        vm.Rebuild(today); vm.ApplyVisibility(settings, today);
        vm.SettingsCommand = vm.RefreshCommand = vm.ExitCommand = new ActionCommand(() => { });
        var window = new IslandWindow(vm, settings, () => { }) { Title = "QuotaMonitor v1.7.0 · Showcase", ShowInTaskbar = false, Topmost = false };
        typeof(IslandWindow).GetProperty("VerificationSuspendsPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Show(); await Task.Delay(450); SendToBottom(window);

        // Clean four-provider states.
        window.SetState(IslandState.Expanded, false); await Settle(window);
        Save(window, Path.Combine(output, "expanded.png"), 1.5);
        Save(window, Path.Combine(output, "expanded-2x.png"), 2.0);
        SaveGeometry(window, Path.Combine(output, "geometry-expanded-2x.json"), heatmapCrop: true);
        window.SetState(IslandState.Collapsed, false); await Settle(window);
        Save(window, Path.Combine(output, "collapsed.png"), 1.5);
        Save(window, Path.Combine(output, "collapsed-2x.png"), 2.0);
        window.SetState(IslandState.Minimal, false); await Settle(window);
        Save(window, Path.Combine(output, "minimal.png"), 1.5);
        Save(window, Path.Combine(output, "minimal-4x.png"), 4.0);

        // Real WPF day tooltip: hover a date that has invented usage in all four lanes.
        window.SetState(IslandState.Expanded, false); await Settle(window);
        var hoverDay = vm.Grid!.Days.Where(d => d.Lanes.All(l => l.HasUsage) && d.Column >= 18 && d.Column <= 24).LastOrDefault() ?? vm.Grid.Days[^1];
        window.HoverDayForSmoke(hoverDay.Date);
        for (var i = 0; i < 40 && !window.TipVisible; i++) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(25); }
        await Task.Delay(220); window.UpdateLayout();
        if (!window.TipVisible) throw new InvalidOperationException("Tooltip did not appear for " + hoverDay.Date);
        Save(window, Path.Combine(output, "tooltip.png"), 1.5);
        Save(window, Path.Combine(output, "tooltip-2x.png"), 2.0);
        SaveGeometry(window, Path.Combine(output, "geometry-tooltip.json"), heatmapCrop: false, tip: true);
        window.HoverDayForSmoke(DateOnly.MinValue); await Settle(window);

        // Alert Minimal: low invented remaining quota / balance, same production alert evaluator.
        ResetProviders(vm, now, alerts: true); vm.UpdateAlerts(settings, now);
        window.SetState(IslandState.Minimal, false); await Settle(window);
        Save(window, Path.Combine(output, "alert-minimal.png"), 1.5);
        Save(window, Path.Combine(output, "alert-minimal-2x.png"), 2.0);
        Save(window, Path.Combine(output, "alert-minimal-3x.png"), 3.0);

        // Provider-mask layouts: 1, 2, 3 and 4 visible providers, plus the OpenCode Go-only view.
        ResetProviders(vm, now, alerts: false); vm.UpdateAlerts(settings, now);
        foreach (var (mask, count) in new[] { (1, 1), (3, 2), (13, 3), (15, 4) })
        {
            ApplyMask(vm, settings, today, mask);
            window.SetState(IslandState.Expanded, false); await Settle(window);
            Save(window, Path.Combine(output, $"layout-{count}.png"), 1.5);
        }
        ApplyMask(vm, settings, today, 2);
        window.SetState(IslandState.Expanded, false); await Settle(window);
        Save(window, Path.Combine(output, "opencode-go.png"), 1.5);
        Save(window, Path.Combine(output, "opencode-go-2x.png"), 2.0);
        SaveGeometry(window, Path.Combine(output, "geometry-opencode-go.json"), heatmapCrop: true);

        // Fallback morph frames from the unchanged production visual tree (not a screen recording).
        ApplyMask(vm, settings, today, 15);
        var frames = Path.Combine(output, "frames"); Directory.CreateDirectory(frames);
        window.SetState(IslandState.Minimal, false); await Settle(window);
        for (var i = 0; i < 120; i++)
        {
            if (i == 25) window.SetState(IslandState.Collapsed);
            if (i == 55) window.SetState(IslandState.Expanded);
            if (i == 100) window.SetState(IslandState.Minimal);
            await Task.Delay(40); Save(window, Path.Combine(frames, $"{i:D4}.png"), 1.0);
        }

        // Settings presentation: four real tabs, isolated empty store, offline HTTP handler.
        var dialogSettings = new MonitorSettings { TopCenter = true, HideInFullscreen = true, AutoStart = false, CodexExecutable = "", DshSessionRoot = "" };
        using var http = new HttpClient(new OfflineHandler());
        var dialog = new SettingsWindow(dialogSettings, new SettingsStore(Path.Combine(output, "settings-empty-store")), new CredentialStore("QuotaMonitor/Showcase/" + Guid.NewGuid()), http, () => { }, () => "deepseek-flash · synthetic showcase", startupAllowed: false)
        { ShowInTaskbar = false, ShowActivated = false };
        dialog.Show(); await Task.Delay(350); SendToBottom(dialog);
        foreach (var (nav, file, expander) in new[]
        {
            ("GeneralNav", "settings-general.png", false),
            ("ProvidersNav", "settings-providers.png", false),
            ("AlertsNav", "settings-alerts.png", false),
            ("AdvancedNav", "settings-advanced.png", true)
        })
        {
            if (dialog.FindName(nav) is not RadioButton button) throw new InvalidOperationException("Missing settings nav " + nav);
            button.IsChecked = true; await Task.Delay(320);
            if (expander)
            {
                if (dialog.FindName("RefreshExpander") is Expander refresh) refresh.IsExpanded = true;
                if (dialog.FindName("PathsExpander") is Expander paths) paths.IsExpanded = true;
                await Task.Delay(240);
            }
            dialog.UpdateLayout(); SendToBottom(dialog);
            Save(dialog, Path.Combine(output, file), 1.5);
        }
        SaveSettingsGeometry(dialog, Path.Combine(output, "geometry-settings.json"));
        dialog.Close();
        window.Close();

        File.WriteAllText(Path.Combine(output, "render-meta.json"), JsonSerializer.Serialize(new
        {
            Tool = "QuotaMonitor.Showcase", Version = "1.7.0", GeneratedUtc = now,
            Method = "RenderTargetBitmap of unchanged production WPF views (offline; no adapters, network, account, credential or production store access). Not a screen recording.",
            DpiScale = 1.5, Synthetic = true,
            Providers = new object[]
            {
                new { Id = "codex", Name = "Codex", RemainingPercent = 72, Windows = new[] { new { Name = "Primary", UsedPercent = 28, RemainingPercent = 72, DurationMinutes = (long?)300 }, new { Name = "Secondary", UsedPercent = 20, RemainingPercent = 80, DurationMinutes = (long?)10080 } } },
                new { Id = "dsh", Name = "DSH", Balance = "CNY 42.80", Bonus = "CNY 5.00" },
                new { Id = "antigravity", Name = "Antigravity", RemainingPercent = 68, Windows = new[] { new { Name = "Gemini", UsedPercent = 32, RemainingPercent = 68, DurationMinutes = (long?)10080 }, new { Name = "Claude/GPT", UsedPercent = 46, RemainingPercent = 54, DurationMinutes = (long?)10080 } } },
                new { Id = "opencode-go", Name = "OpenCode Go", RemainingPercent = 64, Windows = new[] { new { Name = "5 小时", UsedPercent = 18, RemainingPercent = 82, DurationMinutes = (long?)300 }, new { Name = "每周", UsedPercent = 24, RemainingPercent = 76, DurationMinutes = (long?)10080 }, new { Name = "每月", UsedPercent = 36, RemainingPercent = 64, DurationMinutes = (long?)null } } }
            },
            AlertScenario = "Codex 8/12 %, OpenCode Go 12/9/6 %, DSH CNY 0.80, Antigravity 15/8 % remaining with production default thresholds; all invented.",
            TooltipDate = hoverDay.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Captures = new[] { "expanded.png", "expanded-2x.png", "collapsed.png", "minimal.png", "tooltip.png", "tooltip-2x.png", "alert-minimal.png", "alert-minimal-2x.png", "layout-1.png", "layout-2.png", "layout-3.png", "layout-4.png", "opencode-go.png", "opencode-go-2x.png", "frames/0000..0119.png", "settings-general.png", "settings-providers.png", "settings-alerts.png", "settings-advanced.png" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "provenance.txt"), "Rendered from unchanged production WPF views (offline RenderTargetBitmap). All balances, quota, history and account fields are invented synthetic values. No adapters, account files, production store, credentials or network. Settings: isolated empty store, random nonexistent credential target, startup inspection disabled. No screen capture; not a desktop recording.");
    }

    // Coherent invented snapshots shared by every static asset.
    static void ResetProviders(MonitorViewModel vm, DateTimeOffset now, bool alerts)
    {
        vm.Codex.Update(new ProviderSnapshot
        {
            ProviderId = "codex", DisplayName = "Codex", MetricType = MetricType.Quota, ConnectionStatus = ConnectionStatus.Ready,
            LastUpdated = now, UsageUpdated = now, RemainingPercent = alerts ? 8 : 72,
            Windows = alerts
                ? [new("codex", "Primary", 92, 300, now.AddHours(2)), new("codex", "Secondary", 88, 10080, now.AddDays(3))]
                : [new("codex", "Primary", 28, 300, now.AddHours(2)), new("codex", "Secondary", 20, 10080, now.AddDays(3))],
            TodayTokens = 384000, TodayRequests = 42, InputTokens = 310000, OutputTokens = 74000, CurrentModel = "synthetic-model", DataSource = "Synthetic showcase"
        });
        vm.Dsh.Update(new ProviderSnapshot
        {
            ProviderId = "dsh", DisplayName = "DSH", MetricType = MetricType.Balance, ConnectionStatus = ConnectionStatus.Ready,
            LastUpdated = now, UsageUpdated = now, RemainingBalance = alerts ? 0.80m : 42.80m, Currency = "CNY",
            TodayTokens = 612000, TodayRequests = 63, InputTokens = 470000, OutputTokens = 142000, CurrentModel = "deepseek-synthetic", DataSource = "Synthetic showcase",
            AccountBalance = new(new([new("CNY", alerts ? "0.80" : "42.80")], [new("CNY", "5.00")], now, "synthetic showcase"), DshBalanceState.Ready, null)
        });
        vm.Antigravity.Update(new ProviderSnapshot
        {
            ProviderId = "antigravity", DisplayName = "Antigravity", MetricType = MetricType.Quota, ConnectionStatus = ConnectionStatus.Ready,
            LastUpdated = now, UsageUpdated = now, RemainingPercent = alerts ? 8 : 68,
            Windows = alerts
                ? [new("gemini-weekly", "Gemini", 85, 10080, now.AddDays(4)), new("3p-weekly", "Claude/GPT", 92, 10080, now.AddDays(5))]
                : [new("gemini-weekly", "Gemini", 32, 10080, now.AddDays(4)), new("3p-weekly", "Claude/GPT", 46, 10080, now.AddDays(5))],
            TodayTokens = 226000, TodayRequests = 31, InputTokens = 180000, OutputTokens = 46000, CurrentModel = "synthetic-model", DataSource = "Synthetic showcase"
        });
        vm.Go.Update(new ProviderSnapshot
        {
            ProviderId = "opencode-go", DisplayName = "OpenCode Go", MetricType = MetricType.Quota, ConnectionStatus = ConnectionStatus.Ready,
            LastUpdated = now, UsageUpdated = now, RemainingPercent = alerts ? 6 : 64,
            Windows = alerts
                ? [new("rolling", "5 小时", 88, 300, now.AddHours(2)), new("weekly", "每周", 91, 10080, now.AddDays(2)), new("monthly", "每月", 94, null, now.AddDays(12))]
                : [new("rolling", "5 小时", 18, 300, now.AddHours(2)), new("weekly", "每周", 24, 10080, now.AddDays(2)), new("monthly", "每月", 36, null, now.AddDays(12))],
            TodayTokens = 468000, TodayRequests = 57, InputTokens = 350000, OutputTokens = 118000, CurrentModel = "synthetic-model", DataSource = "Synthetic showcase"
        });
    }

    static void ApplyMask(MonitorViewModel vm, MonitorSettings settings, DateOnly today, int mask)
    {
        settings.ShowCodex = (mask & 1) != 0; settings.ShowGo = (mask & 2) != 0;
        settings.ShowDsh = (mask & 4) != 0; settings.ShowAntigravity = (mask & 8) != 0;
        vm.ApplyVisibility(settings, today);
    }

    static async Task Settle(Window window)
    {
        for (var i = 0; i < 4; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(260);
        window.UpdateLayout(); SendToBottom(window); await Task.Delay(40);
    }

    static void SendToBottom(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, Bottom, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
    }

    static void Save(FrameworkElement element, string path, double scale)
    {
        element.UpdateLayout();
        var width = element.ActualWidth; var height = element.ActualHeight;
        if (width < 1 || height < 1) throw new InvalidOperationException("Empty capture: " + path);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    static void SaveGeometry(Window window, string path, bool heatmapCrop, bool tip = false)
    {
        object? Rect(string name)
        {
            if (window.FindName(name) is not FrameworkElement element) return null;
            var bounds = element.TransformToVisual(window).TransformBounds(new Rect(element.RenderSize));
            return new { X = Math.Round(bounds.X, 2), Y = Math.Round(bounds.Y, 2), W = Math.Round(bounds.Width, 2), H = Math.Round(bounds.Height, 2), Visible = element.IsVisible };
        }
        object? Crop = null;
        if (window.FindName("Island") is FrameworkElement island && island is MorphSurface surface)
        {
            var clip = island.TransformToVisual(window).TransformBounds(surface.ContentClip.Bounds);
            Crop = new { X = Math.Round(clip.X, 2), Y = Math.Round(clip.Y, 2), W = Math.Round(clip.Width, 2), H = Math.Round(clip.Height, 2) };
        }
        var data = new
        {
            Window = new { W = Math.Round(window.ActualWidth, 2), H = Math.Round(window.ActualHeight, 2), Dpi = Math.Round(VisualTreeHelper.GetDpi(window).DpiScaleX, 3) },
            Scale = 2.0,
            Island = Rect("Island"), ContentClip = Crop, Details = Rect("Details"), Strip = Rect("Strip"),
            HeatmapHeading = heatmapCrop ? Rect("HeatmapHeading") : null, Heatmap = heatmapCrop ? Rect("Heatmap") : null,
            Legend = heatmapCrop ? Rect("HarnessLegend") : null, TipCard = tip ? Rect("TipCard") : null
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void SaveSettingsGeometry(SettingsWindow dialog, string path)
    {
        object? Rect(string name)
        {
            if (dialog.FindName(name) is not FrameworkElement element) return null;
            var bounds = element.TransformToVisual(dialog).TransformBounds(new Rect(element.RenderSize));
            return new { X = Math.Round(bounds.X, 2), Y = Math.Round(bounds.Y, 2), W = Math.Round(bounds.Width, 2), H = Math.Round(bounds.Height, 2), Visible = element.IsVisible };
        }
        var data = new
        {
            Window = new { W = Math.Round(dialog.ActualWidth, 2), H = Math.Round(dialog.ActualHeight, 2), Dpi = Math.Round(VisualTreeHelper.GetDpi(dialog).DpiScaleX, 3) },
            Scale = 1.5,
            PageTitle = Rect("PageTitle"), PageSubtitle = Rect("PageSubtitle"), PageScroll = Rect("PageScroll"),
            General = Rect("GeneralPage"), Providers = Rect("ProvidersPage"), Alerts = Rect("AlertsPage"), Advanced = Rect("AdvancedPage"),
            GeneralNav = Rect("GeneralNav"), ProvidersNav = Rect("ProvidersNav"), AlertsNav = Rect("AlertsNav"), AdvancedNav = Rect("AdvancedNav")
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Network disabled in showcase");
    }
}
