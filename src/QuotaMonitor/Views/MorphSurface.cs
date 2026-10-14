using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace QuotaMonitor.Views;

public class MorphLayoutHost : Grid
{
    private Geometry? retainedClip;
    public void SetRenderClip(Geometry geometry){retainedClip=geometry;VisualClip=geometry;}
    protected override Geometry? GetLayoutClip(Size layoutSlotSize)=>retainedClip;
    public int Measures { get; private set; }
    public int Arranges { get; private set; }
    protected override Size MeasureOverride(Size availableSize){Measures++;return base.MeasureOverride(availableSize);}
    protected override Size ArrangeOverride(Size finalSize){Arranges++;return base.ArrangeOverride(finalSize);}
}

// Geometry is in device-independent coordinates on a fixed HWND. Changing a pose
// invalidates drawing only: no Measure/Arrange, HWND resize, or live blur shader.
public class MorphSurface : FrameworkElement
{
    private readonly DrawingVisual drawing=new();
    public MorphSurface(){AddVisualChild(drawing);}
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)=>Outline.FillContains(parameters.HitPoint)?new PointHitTestResult(this,parameters.HitPoint):null;
    protected override int VisualChildrenCount=>1;
    protected override Visual GetVisualChild(int index)=>index==0?drawing:throw new ArgumentOutOfRangeException(nameof(index));
    public Rect ShellBounds { get; private set; } = new(0,0,176,8);
    public CornerRadius CornerRadius { get; private set; } = new(1,1,5,5);
    public Geometry Outline { get; private set; } = Geometry.Empty;
    private Geometry inner = Geometry.Empty;
    private readonly SolidColorBrush fill = new(), stroke = new();
    private double amount;
    private BitmapSource[]? shadow;
    private double shadowDpi;
    public int PoseCount { get; private set; }
    public int ShadowBuilds { get; private set; }
    public Geometry ContentClip => inner;
    public void PrepareShadow(double dpi)
    {
        if (shadow is not null && Math.Abs(shadowDpi-dpi)<.001) return;
        shadowDpi=dpi;ShadowBuilds++;
        const double gutter=36,radius=29,size=132;
        // Blur the alpha silhouette itself, then offset it. DropShadowEffect also
        // composites the opaque source, so its output cannot be used as a shadow cache.
        var silhouette=new DrawingVisual { Effect=new BlurEffect{Radius=22,KernelType=KernelType.Gaussian,RenderingBias=RenderingBias.Quality},Opacity=.5,Transform=new TranslateTransform(0,5) };
        using(var dc=silhouette.RenderOpen())dc.DrawRoundedRectangle(Brushes.Black,null,new(gutter,gutter,size-2*gutter,size-2*gutter),radius,radius);
        var pixels=(int)Math.Ceiling(size*dpi);
        var rendered=new RenderTargetBitmap(pixels,pixels,96*dpi,96*dpi,PixelFormats.Pbgra32);rendered.Render(silhouette);
        rendered.Freeze();var image=rendered;
        var cuts=new[]{0,(int)Math.Round(65*dpi),(int)Math.Round(67*dpi),pixels};
        shadow=new BitmapSource[9];
        for(var y=0;y<3;y++)for(var x=0;x<3;x++) { var piece=new CroppedBitmap(image,new Int32Rect(cuts[x],cuts[y],cuts[x+1]-cuts[x],cuts[y+1]-cuts[y]));piece.Freeze();shadow[y*3+x]=piece; }
    }
    public void Pose(Rect bounds,double expansion,double alert)
    {
        PoseCount++;ShellBounds=bounds;amount=Math.Clamp(expansion,0,1);
        // Top remains attached for both minimal forms, becomes the original round pill at full expansion.
        CornerRadius=new(1+28*amount,1+28*amount,5+8*alert+24*amount*(1-alert),5+8*alert+24*amount*(1-alert));
        Outline=RoundedSilhouette.Create(bounds.Width,bounds.Height,CornerRadius);
        var moved=Outline.Clone();moved.Transform=new TranslateTransform(bounds.X,bounds.Y);moved.Freeze();Outline=moved;
        var innerRect=new Rect(bounds.X+1,bounds.Y+amount,Math.Max(0,bounds.Width-2),Math.Max(0,bounds.Height-1-amount));
        var clip=RoundedSilhouette.Create(innerRect.Width,innerRect.Height,new(Math.Max(0,CornerRadius.TopLeft-1),Math.Max(0,CornerRadius.TopRight-1),Math.Max(0,CornerRadius.BottomRight-1),Math.Max(0,CornerRadius.BottomLeft-1))).Clone();
        clip.Transform=new TranslateTransform(innerRect.X,innerRect.Y);clip.Freeze();inner=clip;
        static byte Mix(byte a,byte b,double t)=>(byte)Math.Round(a+(b-a)*t);
        fill.Color=Color.FromRgb(Mix(20,0,amount),Mix(23,0,amount),Mix(28,0,amount));
        stroke.Color=Color.FromArgb(Mix(255,46,amount),Mix(69,255,amount),Mix(75,255,amount),Mix(86,255,amount));
        using(var dc=drawing.RenderOpen())DrawShell(dc);
    }
    private void DrawShell(DrawingContext dc)
    {
        if(shadow is not null && amount>0)
        {
            dc.PushOpacity(amount*amount);
            var b=ShellBounds;var corner=36+Math.Min(29,b.Height/2);
            var xs=new[]{b.Left-36,b.Left-36+corner,b.Right+36-corner,b.Right+36};
            var ys=new[]{b.Top-36,b.Top-36+corner,b.Bottom+36-corner,b.Bottom+36};
            for(var y=0;y<3;y++)for(var x=0;x<3;x++)if(xs[x+1]>xs[x]&&ys[y+1]>ys[y])dc.DrawImage(shadow[y*3+x],new(xs[x],ys[y],xs[x+1]-xs[x],ys[y+1]-ys[y]));
            dc.Pop();
        }
        dc.DrawGeometry(stroke,null,Outline);
        dc.DrawGeometry(fill,null,inner);
    }
}
