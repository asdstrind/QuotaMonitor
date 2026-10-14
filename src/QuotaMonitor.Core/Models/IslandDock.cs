namespace QuotaMonitor.Core.Models;

public readonly record struct DockBounds(int Left,int Top,int Right,int Bottom);
public static class IslandDock
{
    public static DockBounds Place(DockBounds area,double scale,double width,double height,bool centered,double savedLeft,double topInset)
    {
        var w=Math.Clamp((int)Math.Ceiling(width*scale),1,area.Right-area.Left);
        var h=Math.Clamp((int)Math.Ceiling(height*scale),1,area.Bottom-area.Top);
        // A custom location is the left edge of the original 520 DIP capsule. All states share its center.
        var anchor=centered?(area.Left+area.Right)/2d:savedLeft+520*scale/2;
        var left=Math.Clamp((int)Math.Round(anchor-w/2d),area.Left,area.Right-w);
        var top=Math.Clamp(area.Top+(int)Math.Round(topInset*scale),area.Top,area.Bottom-h);
        return new(left,top,left+w,top+h);
    }
}
