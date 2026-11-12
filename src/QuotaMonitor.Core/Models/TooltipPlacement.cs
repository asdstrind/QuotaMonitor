namespace QuotaMonitor.Core.Models;

public readonly record struct TipRect(double X,double Y,double Width,double Height)
{
    public double Right=>X+Width;
    public double Bottom=>Y+Height;
    public bool Contains(TipRect r)=>r.X>=X&&r.Y>=Y&&r.Right<=Right&&r.Bottom<=Bottom;
    public double Overlap(TipRect r)=>Math.Max(0,Math.Min(Right,r.Right)-Math.Max(X,r.X))*Math.Max(0,Math.Min(Bottom,r.Bottom)-Math.Max(Y,r.Y));
    public TipRect Inset(double n)=>new(X+n,Y+n,Math.Max(0,Width-2*n),Math.Max(0,Height-2*n));
}

public static class TooltipPlacement
{
    // A rectangle inscribed in a rounded content clip. At each corner its distance to
    // the circular arc is nonnegative; this also protects fractional device pixels.
    public static TipRect SafeBounds(TipRect clip,double radius,double deviceScale)=>clip.Inset(
        Math.Max(0,radius)*(1-Math.Sqrt(.5))+1/Math.Max(.01,deviceScale));

    // Size includes the effect's visual footprint. All coordinates use the overlay's DIP space.
    public static TipRect? Place(TipRect safe,double width,double height,TipRect tile,TipRect heatmap,double gap)
    {
        if(width<=0||height<=0||width>safe.Width||height>safe.Height||!double.IsFinite(width+height))return null;
        double X(double x)=>Math.Clamp(x,safe.X,safe.Right-width);
        double Y(double y)=>Math.Clamp(y,safe.Y,safe.Bottom-height);
        var centerX=X(tile.X+tile.Width/2-width/2);var centerY=Y(tile.Y+tile.Height/2-height/2);
        var above=new TipRect(centerX,tile.Y-gap-height,width,height);
        // Both remain above the hovered date. Use the closest one that avoids more of
        // the calendar, instead of unnecessarily covering many rows for bottom dates.
        var aboveCalendar=new TipRect(centerX,Math.Min(tile.Y,heatmap.Y)-gap-height,width,height);
        var upper=new[]{above,aboveCalendar}.Where(safe.Contains).Where(r=>r.Overlap(tile)==0)
            .OrderBy(r=>r.Overlap(heatmap)).ThenByDescending(r=>r.Y).Select(r=>(TipRect?)r).FirstOrDefault();
        if(upper is not null)return upper;
        var candidates=new[]{
            new TipRect(centerX,tile.Bottom+gap,width,height),
            new TipRect(tile.X-gap-width,centerY,width,height),new TipRect(tile.Right+gap,centerY,width,height),
            new TipRect(centerX,safe.Y,width,height),new TipRect(centerX,safe.Bottom-height,width,height),
            new TipRect(safe.X,centerY,width,height),new TipRect(safe.Right-width,centerY,width,height)};
        return candidates.Where(safe.Contains).Where(r=>r.Overlap(tile)==0)
            .OrderBy(r=>r.Overlap(heatmap)).ThenBy(r=>Math.Abs(r.X-tile.X)+Math.Abs(r.Y-tile.Y))
            .Select(r=>(TipRect?)r).FirstOrDefault();
    }
}
