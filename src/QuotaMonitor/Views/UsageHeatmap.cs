using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuotaMonitor.Core.Models;
namespace QuotaMonitor.Views;

public sealed class HeatmapHoverEventArgs(GridDay? day, Rect tile) : EventArgs
{
    public GridDay? Day { get; } = day;
    public Rect Tile { get; } = tile;
}

/// <summary>
/// Week-column calendar where every day is a glyph of three capsules, one per platform in fixed
/// order. Capsule height is log-scaled tokens; a dot is a recorded zero; an empty slot has no
/// record; a hollow capsule is estimated; a corner pip marks a day with unverifiable records.
/// </summary>
public sealed class UsageHeatmap : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(UsageGrid), typeof(UsageHeatmap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((UsageHeatmap)d).DataChanged()));
    public static readonly DependencyProperty RelativeProperty = DependencyProperty.Register(nameof(Relative), typeof(bool), typeof(UsageHeatmap), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FocusLaneProperty = DependencyProperty.Register(nameof(FocusLane), typeof(string), typeof(UsageHeatmap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(UsageHeatmap), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public UsageGrid? Data { get => (UsageGrid?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public bool Relative { get => (bool)GetValue(RelativeProperty); set => SetValue(RelativeProperty, value); }
    public string? FocusLane { get => (string?)GetValue(FocusLaneProperty); set => SetValue(FocusLaneProperty, value); }
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    public event EventHandler<HeatmapHoverEventArgs>? Hovered;

    public const double Gutter = 26, MonthBand = 20, RowPitch = 20, BarWidth = 4, BarGap = 2, MaxBar = 15, MinBar = 7, Dot = 4;
    private const double GlyphWidth = 3 * BarWidth + 2 * BarGap;
    private static readonly Color[] Accents = [Color.FromRgb(0x19, 0xA6, 0x7A), Color.FromRgb(0xE2, 0x67, 0x2F), Color.FromRgb(0x4A, 0x8A, 0xF0)];
    private static readonly string[] Ids = ["codex", "claude", "dsh"];
    private static readonly string[] WeekdayLabels = ["一", "", "三", "", "五", "", ""];
    private static readonly Brush Label = Frozen(Color.FromRgb(0x7A, 0x80, 0x8A));
    private static readonly Brush Pip = Frozen(Color.FromRgb(0xC9, 0xCD, 0xD4));
    private static readonly Brush HoverFill = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Pen TodayPen = FrozenPen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Small, Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Dictionary<(int Lane, byte Alpha), Brush> fills = [];
    private readonly Dictionary<(int Lane, byte Alpha), Pen> strokes = [];
    private GridDay? hovered;

    public UsageHeatmap() { Cursor = Cursors.Arrow; SnapsToDevicePixels = true; }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double width) { var p = new Pen(Frozen(c), width); p.Freeze(); return p; }
    private Brush Fill(int lane, double alpha)
    {
        var a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        if (!fills.TryGetValue((lane, a), out var brush)) fills[(lane, a)] = brush = Frozen(Color.FromArgb(a, Accents[lane].R, Accents[lane].G, Accents[lane].B));
        return brush;
    }
    private Pen Stroke(int lane, double alpha)
    {
        var a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        if (!strokes.TryGetValue((lane, a), out var pen)) strokes[(lane, a)] = pen = FrozenPen(Color.FromArgb(a, Accents[lane].R, Accents[lane].G, Accents[lane].B), 1);
        return pen;
    }

    private void DataChanged()
    {
        // Keep the hovered day in sync with the new grid so the tooltip never shows stale values.
        if (hovered is null || Data is not { } grid) return;
        hovered = grid.DayAt(hovered.Column, hovered.Row);
        Hovered?.Invoke(this, new(hovered, hovered is null ? Rect.Empty : Tile(hovered)));
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 640 : available.Width, MonthBand + 7 * RowPitch);
    private double Pitch => (ActualWidth - Gutter) / Math.Max(1, Data?.Weeks ?? UsageGrid.DefaultWeeks);
    private double GlyphLeft(int column) => Gutter + column * Pitch + (Pitch - GlyphWidth) / 2;
    public Rect Tile(GridDay day) => new(GlyphLeft(day.Column) - 4, MonthBand + day.Row * RowPitch + 1, GlyphWidth + 8, RowPitch - 2);
    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

    protected override void OnRender(DrawingContext dc)
    {
        var grid = Data;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (grid is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var month in grid.Months)
            dc.DrawText(Text($"{month.Month.Month}月", dpi), new Point(GlyphLeft(month.Column), 1));
        for (var row = 0; row < 7; row++)
            if (WeekdayLabels[row].Length > 0) { var t = Text(WeekdayLabels[row], dpi); dc.DrawText(t, new Point(0, MonthBand + row * RowPitch + (RowPitch - t.Height) / 2)); }
        var focus = FocusLane;
        var reveal = Math.Clamp(Reveal, 0, 1);
        foreach (var day in grid.Days)
        {
            var tile = Tile(day);
            if (ReferenceEquals(day, hovered) || (hovered is not null && hovered.Date == day.Date)) dc.DrawRoundedRectangle(HoverFill, null, tile, 5, 5);
            if (day.IsToday) dc.DrawRoundedRectangle(null, TodayPen, new Rect(tile.X + 0.5, tile.Y + 0.5, tile.Width - 1, tile.Height - 1), 5, 5);
            // The open sweep: week columns rise left to right, each from a dot to its height.
            var local = Ease(Math.Clamp(reveal * 1.7 - day.Column / (double)grid.Weeks * 0.7, 0, 1));
            var baseline = MonthBand + day.Row * RowPitch + RowPitch - 3;
            var left = GlyphLeft(day.Column);
            var pip = false;
            for (var i = 0; i < day.Lanes.Count && i < 3; i++)
            {
                var lane = day.Lanes[i];
                var dim = focus is not null && focus != Ids[i];
                var alpha = dim ? 0.16 : 1;
                var x = left + i * (BarWidth + BarGap);
                switch (lane.State)
                {
                    case LaneState.Missing: continue;
                    case LaneState.Zero:
                        dc.DrawEllipse(Fill(i, 0.42 * alpha), null, new Point(x + Dot / 2, baseline - Dot / 2), Dot / 2, Dot / 2);
                        continue;
                }
                var level = Relative ? lane.RelativeLevel : lane.Level;
                var normalized = Math.Clamp((level - UsageGrid.MinLevel) / (1 - UsageGrid.MinLevel), 0, 1);
                var height = Dot + (MinBar + (MaxBar - MinBar) * normalized - Dot) * local;
                var rect = new Rect(x, baseline - height, BarWidth, height);
                if (lane.State == LaneState.Estimated)
                    dc.DrawRoundedRectangle(Fill(i, 0.22 * alpha), Stroke(i, alpha), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1), 1.5, 1.5);
                else dc.DrawRoundedRectangle(Fill(i, alpha), null, rect, BarWidth / 2, BarWidth / 2);
                if (!dim && (lane.State == LaneState.Partial || lane.Excluded > 0)) pip = true;
            }
            if (pip) dc.DrawEllipse(Pip, null, new Point(tile.Right - 2.5, tile.Top + 2.5), 1.4, 1.4);
        }
    }

    private static FormattedText Text(string text, double dpi) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Label, dpi);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Data is not { } grid) return;
        var p = e.GetPosition(this);
        var column = (int)Math.Floor((p.X - Gutter) / Pitch);
        var row = (int)Math.Floor((p.Y - MonthBand) / RowPitch);
        var day = p.X < Gutter || p.Y < MonthBand ? null : grid.DayAt(column, row);
        if (day?.Date == hovered?.Date) return;
        hovered = day;
        InvalidateVisual();
        Hovered?.Invoke(this, new(day, day is null ? Rect.Empty : Tile(day)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hovered is null) return;
        hovered = null;
        InvalidateVisual();
        Hovered?.Invoke(this, new(null, Rect.Empty));
    }

    /// <summary>Integration-test hook: hover a date exactly as the pointer would.</summary>
    public GridDay? HoverDate(DateOnly date)
    {
        hovered = Data?.Days.FirstOrDefault(d => d.Date == date);
        InvalidateVisual();
        Hovered?.Invoke(this, new(hovered, hovered is null ? Rect.Empty : Tile(hovered)));
        return hovered;
    }
}
