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
        public Brush Ink=null!;
    }
    private readonly List<Slot> slots=[];
    public double CapsuleWidth { get; private set; }=176;
    public bool Active=>slots.Any(s=>s.X.Active||s.Alpha.Active);
    public void Update(IReadOnlyList<LowQuotaAlert> alerts,double now,bool animate)
    {
        var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface=new Typeface((FontFamily)FindResource("NumFont"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
        var runs=alerts.Select(a=>(Alert:a,Text:new FormattedText(a.Value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,16,(Brush)new BrushConverter().ConvertFromString(a.Color)!,dpi))).ToArray();
        var width=runs.Sum(r=>16+7+r.Text.WidthIncludingTrailingWhitespace)+Math.Max(0,runs.Length-1)*18+28;
        CapsuleWidth=runs.Length==0?176:Math.Max(76,width);
        var x=-width/2+14;
        foreach(var run in runs)
        {
            var key=run.Alert.Key.StartsWith("codex",StringComparison.Ordinal)?"codex":run.Alert.Key;
            var slot=slots.FirstOrDefault(s=>s.Key==key);
            if(slot is null) { slot=new(key,x);slots.Add(slot); }
            slot.Text=run.Text;slot.Ink=(Brush)new BrushConverter().ConvertFromString(run.Alert.Color)!;slot.Ink.Freeze();
            if(animate){slot.X.Retarget(x,now,.24);slot.Alpha.Retarget(1,now,.18);}else{slot.X.Set(x);slot.Alpha.Set(1);}
            x+=16+7+run.Text.WidthIncludingTrailingWhitespace+18;
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
        using var dc=drawing.RenderOpen();
        foreach(var slot in slots)
        {
            dc.PushOpacity(slot.Alpha.Value);dc.PushTransform(new TranslateTransform(ActualWidth/2+slot.X.Value,0));
            HarnessIcons.Draw(dc,slot.Key,new Rect(0,5,16,16));
            dc.DrawText(slot.Text,new(23,(26-slot.Text.Height)/2));dc.Pop();dc.Pop();
        }
    }
}
