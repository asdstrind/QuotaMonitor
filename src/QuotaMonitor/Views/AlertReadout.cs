using System.Globalization;
using System.Windows;
using System.Windows.Media;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Views;

// Retained keyed slots prevent a refresh/count change from rebuilding the visual tree.
public class AlertReadout : FrameworkElement
{
    private readonly DrawingVisual drawing=new();
    public AlertReadout(){AddVisualChild(drawing);}
    protected override int VisualChildrenCount=>1;
    protected override Visual GetVisualChild(int index)=>index==0?drawing:throw new ArgumentOutOfRangeException(nameof(index));
    protected override void OnRenderSizeChanged(SizeChangedInfo info){base.OnRenderSizeChanged(info);DrawFrame();}
    private sealed class Slot(string key,double x)
    {
        public string Key=key;
        public MorphMotion X=new(x), Alpha=new(0);
        public FormattedText Text=null!;
        public FormattedText? Label,SecondLabel,Second;
        public double ColumnWidth;
        public Brush Ink=null!;
    }
    private readonly List<Slot> slots=[];
    internal sealed record Placement(string Label,Rect LabelBounds,Rect ValueBounds,double LabelBaseline,double ValueBaseline);
    internal List<Placement> WeeklyPlacements {get;}=[];
    public double CapsuleWidth { get; private set; }=176;
    public bool Active=>slots.Any(s=>s.X.Active||s.Alpha.Active);
    public void Update(IReadOnlyList<LowQuotaAlert> alerts,double now,bool animate)
    {
        var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface=new Typeface((FontFamily)FindResource("NumFont"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
        FormattedText Text(string value,double size,string color)=>new(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,size,(Brush)new BrushConverter().ConvertFromString(color)!,dpi);
        var runs=alerts.Select(a=>{
            var dual=a.SecondValue is not null;
            var weekly=a.Key=="antigravity";
            string ShortLabel(string value)=>value=="Gem"?"Gemini":"Claude/GPT";
            var value=a.Key.StartsWith("codex",StringComparison.Ordinal)?a.Value[(a.Value.IndexOf(' ')+1)..]:a.Value;
            var text=Text(value,16,"#F2F3F5");
            var second=dual?Text(a.SecondValue!,16,"#F2F3F5"):null;
            var label=Text(weekly?ShortLabel(a.Label):a.Key.StartsWith("codex",StringComparison.Ordinal)?(a.Key=="codex-week"?"7d":"5h"):a.Key=="claude"?"USD":"CNY" ,10.5,"#7A808A");
            if(a.Key=="dsh")label=Text(a.Value.StartsWith("¥",StringComparison.Ordinal)?"CNY":"USD",10.5,"#7A808A");
            var secondLabel=dual?Text(ShortLabel(a.SecondLabel!),10.5,"#7A808A"):null;
            var firstWidth=text.WidthIncludingTrailingWhitespace+(label is null?0:label.Width+6);
            var col=dual?Math.Max(firstWidth,second!.WidthIncludingTrailingWhitespace+secondLabel!.Width+6):firstWidth;
            return (Alert:a,Text:text,Second:second,Label:label,SecondLabel:secondLabel,Column:col,Width:23+(dual?2*col+16:col));
        }).ToArray();
        var width=runs.Sum(r=>r.Width)+Math.Max(0,runs.Length-1)*18+28;
        CapsuleWidth=runs.Length==0?176:Math.Max(76,width);
        var x=-width/2+14;
        foreach(var run in runs)
        {
            var key=run.Alert.Key.StartsWith("codex",StringComparison.Ordinal)?"codex":run.Alert.Key;
            var slot=slots.FirstOrDefault(s=>s.Key==key);
            if(slot is null) { slot=new(key,x);slots.Add(slot); }
            slot.Text=run.Text;slot.Ink=(Brush)new BrushConverter().ConvertFromString(run.Alert.Color)!;slot.Ink.Freeze();
            slot.Label=run.Label;slot.SecondLabel=run.SecondLabel;slot.Second=run.Second;slot.ColumnWidth=run.Column;
            if(animate){slot.X.Retarget(x,now,.24);slot.Alpha.Retarget(1,now,.18);}else{slot.X.Set(x);slot.Alpha.Set(1);}
            x+=run.Width+18;
        }
        var keys=runs.Select(r=>r.Alert.Key.StartsWith("codex",StringComparison.Ordinal)?"codex":r.Alert.Key).ToHashSet();
        foreach(var slot in slots.Where(s=>!keys.Contains(s.Key))) { if(animate)slot.Alpha.Retarget(0,now,.18);else slot.Alpha.Set(0); }
        Frame(now);
    }
    public void Frame(double now)
    {
        foreach(var slot in slots){slot.X.Sample(now);slot.Alpha.Sample(now);}
        slots.RemoveAll(s=>s.Alpha.Value==0&&!s.Alpha.Active);DrawFrame();
    }
    private void DrawFrame()
    {
        WeeklyPlacements.Clear();
        using var dc=drawing.RenderOpen();
        foreach(var slot in slots)
        {
            dc.PushOpacity(slot.Alpha.Value);dc.PushTransform(new TranslateTransform(ActualWidth/2+slot.X.Value,0));
            HarnessIcons.Draw(dc,slot.Key,new Rect(0,5,16,16));
            void Weekly(FormattedText label,FormattedText value,double x)
            {
                var y=(26-value.Height)/2;
                dc.DrawText(label,new(x,y+value.Baseline-label.Baseline));
                dc.DrawText(value,new(x+slot.ColumnWidth-value.Width,y));
                if(slot.Key=="antigravity")WeeklyPlacements.Add(new(label.Text,new(x,y+value.Baseline-label.Baseline,label.Width,label.Height),new(x+slot.ColumnWidth-value.Width,y,value.Width,value.Height),y+value.Baseline,y+value.Baseline));
            }
            if(slot.Label is {} label)
            {
                Weekly(label,slot.Text,23);
                if(slot.Second is {} second)
                {
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x33,255,255,255)),1),new(23+slot.ColumnWidth+8,7),new(23+slot.ColumnWidth+8,19));
                    Weekly(slot.SecondLabel!,second,23+slot.ColumnWidth+16);
                }
            }
            else dc.DrawText(slot.Text,new(23,(26-slot.Text.Height)/2));dc.Pop();dc.Pop();
        }
    }
}
