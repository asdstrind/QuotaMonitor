using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuotaMonitor.Core.Models;

namespace QuotaMonitor.Views;

public sealed class HeatmapHoverEventArgs(GridDay? day, Rect tile) : EventArgs
{
    public GridDay? Day { get; } = day;
    public Rect Tile { get; } = tile;
}

public class UsageHeatmap : FrameworkElement
{
    public static readonly DependencyProperty DataProperty=DependencyProperty.Register(nameof(Data),typeof(UsageGrid),typeof(UsageHeatmap),new FrameworkPropertyMetadata(null,(d,_)=>((UsageHeatmap)d).DataChanged()));
    public static readonly DependencyProperty RelativeProperty=DependencyProperty.Register(nameof(Relative),typeof(bool),typeof(UsageHeatmap));
    public static readonly DependencyProperty FocusLaneProperty=DependencyProperty.Register(nameof(FocusLane),typeof(string),typeof(UsageHeatmap),new FrameworkPropertyMetadata(null,(d,e)=>((UsageHeatmap)d).SwitchFocus((string?)e.NewValue)));
    // Retained drawing opacity changes do not invalidate Measure, Arrange or OnRender.
    public static readonly DependencyProperty RevealProperty=DependencyProperty.Register(nameof(Reveal),typeof(double),typeof(UsageHeatmap),new FrameworkPropertyMetadata(1d,(d,_)=>((UsageHeatmap)d).RevealColumns()));
    private static readonly DependencyProperty TransitionProperty=DependencyProperty.Register(nameof(Transition),typeof(double),typeof(UsageHeatmap),new FrameworkPropertyMetadata(1d,(d,_)=>((UsageHeatmap)d).BlendColumns()));
    public UsageGrid? Data {get=>(UsageGrid?)GetValue(DataProperty);set=>SetValue(DataProperty,value);}
    public bool Relative {get=>(bool)GetValue(RelativeProperty);set=>SetValue(RelativeProperty,value);}
    public string? FocusLane {get=>(string?)GetValue(FocusLaneProperty);set=>SetValue(FocusLaneProperty,value);}
    public double Reveal {get=>(double)GetValue(RevealProperty);set=>SetValue(RevealProperty,value);}
    private double Transition=>(double)GetValue(TransitionProperty);
    public event EventHandler<HeatmapHoverEventArgs>? Hovered;
    public const double Gutter=26,MonthBand=20,RowPitch=24,Square=18;
    private static readonly Brush Label=Frozen(Color.FromRgb(0x7A,0x80,0x8A));
    private static readonly Color Empty=Color.FromRgb(0x12,0x15,0x19),Missing=Color.FromRgb(0x05,0x07,0x0A);
    private static readonly Pen TodayPen=FrozenPen(Frozen(Color.FromArgb(0x5C,0xFF,0xFF,0xFF)),1);
    private static readonly Pen HoverPen=FrozenPen(Brushes.White,1.5);
    private static readonly Typeface Face=new(new FontFamily("Segoe UI Variable Small, Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    private readonly List<ContainerVisual> columns=[];
    private readonly List<DrawingVisual> incoming=[],outgoing=[];
    private readonly DrawingVisual labels=new(),highlight=new();
    private readonly Dictionary<string,DrawingGroup[]> cache=[];
    private GridDay? hovered;
    internal int RenderCount {get;private set;}
    internal double RenderCpuMs {get;private set;}
    public UsageHeatmap(){Cursor=Cursors.Arrow;SnapsToDevicePixels=true;AddVisualChild(labels);AddVisualChild(highlight);}
    protected override int VisualChildrenCount=>2+columns.Count;
    protected override Visual GetVisualChild(int index)=>index==0?labels:index==columns.Count+1?highlight:columns[index-1];
    private static Brush Frozen(Color c){var b=new SolidColorBrush(c);b.Freeze();return b;}
    private static Pen FrozenPen(Brush b,double thickness){var p=new Pen(b,thickness);p.Freeze();return p;}
    protected override Size MeasureOverride(Size available)=>new(double.IsInfinity(available.Width)?650:Math.Min(650,available.Width),MonthBand+7*RowPitch);
    private double Pitch=>Math.Max(1,(ActualWidth-Gutter)/Math.Max(1,Data?.Weeks??UsageGrid.DefaultWeeks));
    private double Side=>Math.Max(1,Math.Min(Square,Pitch-4));
    public Rect Tile(GridDay day)=>new(Gutter+day.Column*Pitch+(Pitch-Side)/2,MonthBand+day.Row*RowPitch+(RowPitch-Side)/2,Side,Side);
    protected override void OnRender(DrawingContext dc)=>dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
    protected override void OnRenderSizeChanged(SizeChangedInfo info){base.OnRenderSizeChanged(info);PrepareDrawing();}
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);PrepareDrawing();}
    private void DataChanged()
    {
        PrepareDrawing();
        if(hovered is null||Data is not {} grid)return;
        hovered=grid.DayAt(hovered.Column,hovered.Row);DrawHighlight();Hovered?.Invoke(this,new(hovered,hovered is null?Rect.Empty:Tile(hovered)));
    }
    internal void PrepareDrawing()
    {
        if(ActualWidth<=0||Data is not {} grid)return;
        var start=System.Diagnostics.Stopwatch.GetTimestamp();RenderCount++;
        BeginAnimation(TransitionProperty,null);SetValue(TransitionProperty,1d);
        while(columns.Count<grid.Weeks)
        {
            var column=new ContainerVisual();var old=new DrawingVisual();var next=new DrawingVisual();column.Children.Add(old);column.Children.Add(next);columns.Add(column);outgoing.Add(old);incoming.Add(next);AddVisualChild(column);
        }
        while(columns.Count>grid.Weeks){RemoveVisualChild(columns[^1]);columns.RemoveAt(columns.Count-1);incoming.RemoveAt(incoming.Count-1);outgoing.RemoveAt(outgoing.Count-1);}
        cache.Clear();
        foreach(var focus in new[]{""}.Concat(grid.Providers.Select(p=>p.Id)))
        {
            var groups=Enumerable.Range(0,grid.Weeks).Select(_=>new DrawingGroup()).ToArray();
            // Query data, flags, colors and tile geometry only when data, size or DPI changes.
            for(var col=0;col<grid.Weeks;col++)
            {
                using(var dc=groups[col].Open())foreach(var day in grid.Days.Where(d=>d.Column==col))
                {
                    var lanes=day.Lanes.Where(l=>focus.Length==0||l.ProviderId==focus).ToArray();
                    var lane=focus.Length==0?day.Dominant:lanes.FirstOrDefault();var total=focus.Length==0?day.Total:lane?.Tokens;
                    var missing=lanes.Length==0||lanes.All(l=>l.Tokens is null);
                    var partial=lanes.Any(l=>l.State is LaneState.Missing or LaneState.Partial||l.Excluded>0);
                    if(partial&&total==0)missing=true;
                    var color=Missing;
                    if(total is >0)
                    {
                        var reference=focus.Length==0?grid.SharedReference:grid.Providers.First(p=>p.Id==focus).Reference;
                        var rgb=HeatmapColor.At(lane?.ProviderId??"codex",UsageGrid.TileLevel(total.Value,reference??0));color=Color.FromRgb(rgb.R,rgb.G,rgb.B);
                    }
                    else if(total==0&&!missing)color=Empty;
                    var tile=Tile(day);dc.DrawRoundedRectangle(Frozen(color),null,tile,3,3);
                    if(day.IsToday)dc.DrawRoundedRectangle(null,TodayPen,new(tile.X-1.5,tile.Y-1.5,tile.Width+3,tile.Height+3),4,4);
                }
                groups[col].Freeze();
            }
            cache[focus]=groups;
        }
        using(var dc=labels.RenderOpen())
        {
            var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach(var month in grid.Months)dc.DrawText(Text($"{month.Month.Month}月",dpi),new(Gutter+month.Column*Pitch,1));
            for(var row=0;row<7;row+=2)dc.DrawText(Text(new[]{"一","三","五","日"}[row/2],dpi),new(0,MonthBand+row*RowPitch+2));
        }
        var target=cache.GetValueOrDefault(FocusLane??"")??cache[""];
        for(var i=0;i<columns.Count;i++){using(var dc=incoming[i].RenderOpen())dc.DrawDrawing(target[i]);using var clear=outgoing[i].RenderOpen();}
        BlendColumns();RevealColumns();DrawHighlight();RenderCpuMs+=System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    private void SwitchFocus(string? focus)
    {
        if(!cache.TryGetValue(focus??"",out var target)){PrepareDrawing();return;}
        for(var i=0;i<columns.Count;i++)
        {
            // Retain the actual composite when the pointer redirects a running crossfade.
            var current=new DrawingGroup();using(var dc=current.Open())
            {
                if(outgoing[i].Opacity>0){dc.PushOpacity(outgoing[i].Opacity);dc.DrawDrawing(outgoing[i].Drawing);dc.Pop();}
                if(incoming[i].Opacity>0){dc.PushOpacity(incoming[i].Opacity);dc.DrawDrawing(incoming[i].Drawing);dc.Pop();}
            }current.Freeze();
            using(var dc=outgoing[i].RenderOpen())dc.DrawDrawing(current);
            using(var dc=incoming[i].RenderOpen())dc.DrawDrawing(target[i]);
        }
        BeginAnimation(TransitionProperty,null);SetValue(TransitionProperty,1d);
        if(SystemParameters.ClientAreaAnimation&&IsVisible)BeginAnimation(TransitionProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(160)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
        BlendColumns();if(hovered is not null)Hovered?.Invoke(this,new(hovered,Tile(hovered)));
    }
    private void BlendColumns(){for(var i=0;i<columns.Count;i++){outgoing[i].Opacity=1-Transition;incoming[i].Opacity=Transition;}}
    private void RevealColumns(){for(var i=0;i<columns.Count;i++)columns[i].Opacity=Math.Clamp(Reveal*1.6-i/(double)columns.Count*.6,0,1);}
    private void DrawHighlight(){using var dc=highlight.RenderOpen();if(hovered is null)return;var r=Tile(hovered);dc.DrawRoundedRectangle(null,HoverPen,new(r.X-2,r.Y-2,r.Width+4,r.Height+4),5,5);}
    private static FormattedText Text(string text,double dpi)=>new(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,Face,10.5,Label,dpi);
    internal GridDay? DayAtPoint(Point p)
    {
        if(Data is not {} grid||p.X<Gutter||p.Y<MonthBand)return null;
        var day=grid.DayAt((int)Math.Floor((p.X-Gutter)/Pitch),(int)Math.Floor((p.Y-MonthBand)/RowPitch));return day is not null&&Tile(day).Contains(p)?day:null;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);var day=DayAtPoint(e.GetPosition(this));if(day?.Date==hovered?.Date)return;
        hovered=day;DrawHighlight();Hovered?.Invoke(this,new(day,day is null?Rect.Empty:Tile(day)));
    }
    protected override void OnMouseLeave(MouseEventArgs e){base.OnMouseLeave(e);if(hovered is null)return;hovered=null;DrawHighlight();Hovered?.Invoke(this,new(null,Rect.Empty));}
    public GridDay? HoverDate(DateOnly date){hovered=Data?.Days.FirstOrDefault(d=>d.Date==date);DrawHighlight();Hovered?.Invoke(this,new(hovered,hovered is null?Rect.Empty:Tile(hovered)));return hovered;}
}
