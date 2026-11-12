using System.Xml.Linq;
using System.Windows;
using System.Windows.Media;

namespace QuotaMonitor.Views;

public sealed class HarnessMark : FrameworkElement
{
    public static readonly DependencyProperty ProviderProperty=DependencyProperty.Register(nameof(Provider),typeof(string),typeof(HarnessMark),new FrameworkPropertyMetadata("antigravity",FrameworkPropertyMetadataOptions.AffectsRender));
    public string Provider { get=>(string)GetValue(ProviderProperty);set=>SetValue(ProviderProperty,value); }
    protected override void OnRender(DrawingContext dc)=>HarnessIcons.Draw(dc,Provider,new Rect(RenderSize));
}

// Original path coordinates from official OpenAI, OpenCode and DeepSeek resources.
// Embedded vectors: no runtime requests, raster scaling, or approximate redraws.
internal static class HarnessIcons
{
    private static readonly Brush GoInner=CreateGoInner();
    private static Brush CreateGoInner(){var b=new SolidColorBrush(Color.FromRgb(90,88,88));b.Freeze();return b;}
    private static readonly Dictionary<string,(Geometry Shape,Brush Ink)> icons=Load();
    private static Dictionary<string,(Geometry,Brush)> Load()
    {
        var result=new Dictionary<string,(Geometry,Brush)>();XNamespace ns="http://www.w3.org/2000/svg";
        foreach(var (id,file) in new[]{("codex","codex.svg"),("dsh","deepseek.svg"),("antigravity","antigravity.svg")})
        {
            using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Harness/"+file)).Stream;
            var document=XDocument.Load(stream);
            var paths=id=="dsh"?document.Root!.Elements(ns+"path"):document.Descendants(ns+"path");
            var group=new GeometryGroup{FillRule=FillRule.Nonzero};Brush ink=Brushes.White;
            foreach(var path in paths){group.Children.Add(Geometry.Parse("F1 "+path.Attribute("d")!.Value));if(path.Attribute("fill") is {} color)ink=(Brush)new BrushConverter().ConvertFromString(color.Value)!;}
            group.Freeze();ink.Freeze();result.Add(id,(group,ink));
        }
        var go=Geometry.Parse("F0 M384,416 H128 V96 H384 Z M320,160 H192 V352 H320 Z");go.Freeze();result.Add("opencode-go",(go,Brushes.White));
        return result;
    }
    public static void Draw(DrawingContext dc,string id,Rect bounds)
    {
        var icon=icons[id];var b=icon.Shape.Bounds;var scale=Math.Min(bounds.Width/b.Width,bounds.Height/b.Height);
        dc.PushTransform(new MatrixTransform(scale,0,0,scale,bounds.X+(bounds.Width-b.Width*scale)/2-b.X*scale,bounds.Y+(bounds.Height-b.Height*scale)/2-b.Y*scale));
        if(id=="opencode-go")dc.DrawRectangle(GoInner,null,new Rect(192,224,128,128));
        dc.DrawGeometry(icon.Ink,null,icon.Shape);dc.Pop();
    }
}
