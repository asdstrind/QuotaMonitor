using System.Windows;
using System.Windows.Media;

namespace QuotaMonitor.Views;

// Border.ClipToBounds is rectangular; the content and native input need the same rounded outline.
internal static class RoundedSilhouette
{
    public static Geometry Create(double width, double height, CornerRadius corners)
    {
        width=Math.Max(0,width);height=Math.Max(0,height);
        var tl=Math.Max(0,corners.TopLeft);var tr=Math.Max(0,corners.TopRight);
        var br=Math.Max(0,corners.BottomRight);var bl=Math.Max(0,corners.BottomLeft);
        static double Fit(double extent,double sum)=>sum==0?1:Math.Min(1,extent/sum);
        var fit=Math.Min(Math.Min(Fit(width,tl+tr),Fit(width,bl+br)),Math.Min(Fit(height,tl+bl),Fit(height,tr+br)));
        tl*=fit;tr*=fit;br*=fit;bl*=fit;
        var geometry=new StreamGeometry();
        using(var c=geometry.Open())
        {
            c.BeginFigure(new(tl,0),true,true);
            c.LineTo(new(width-tr,0),true,false);c.ArcTo(new(width,tr),new(tr,tr),0,false,SweepDirection.Clockwise,true,false);
            c.LineTo(new(width,height-br),true,false);c.ArcTo(new(width-br,height),new(br,br),0,false,SweepDirection.Clockwise,true,false);
            c.LineTo(new(bl,height),true,false);c.ArcTo(new(0,height-bl),new(bl,bl),0,false,SweepDirection.Clockwise,true,false);
            c.LineTo(new(0,tl),true,false);c.ArcTo(new(tl,0),new(tl,tl),0,false,SweepDirection.Clockwise,true,false);
        }
        geometry.Freeze();return geometry;
    }
}
