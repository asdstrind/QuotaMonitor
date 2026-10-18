namespace QuotaMonitor.Core.Models;

// Equal OKLCH lightness/chroma at each intensity, with gamut-safe chroma reduction.
public static class HeatmapColor
{
    public static (byte R, byte G, byte B) At(string id, double level)
    {
        var hue = (id switch { "codex" => 162d, "claude" => 45d, "dsh" => 258d, _ => 298d }) * Math.PI / 180;
        level = Math.Clamp(level, 0, 1);
        var l = .27 + .39 * level;
        var chroma = .025 + .13 * level;
        (double R, double G, double B) rgb = default;
        for (var n = 0; n < 30; n++)
        {
            var a = chroma * Math.Cos(hue); var b = chroma * Math.Sin(hue);
            var ll = Math.Pow(l + .3963377774 * a + .2158037573 * b, 3);
            var mm = Math.Pow(l - .1055613458 * a - .0638541728 * b, 3);
            var ss = Math.Pow(l - .0894841775 * a - 1.2914855480 * b, 3);
            rgb = (4.0767416621 * ll - 3.3077115913 * mm + .2309699292 * ss, -1.2684380046 * ll + 2.6097574011 * mm - .3413193965 * ss, -.0041960863 * ll - .7034186147 * mm + 1.7076147010 * ss);
            if (rgb.R is >= 0 and <= 1 && rgb.G is >= 0 and <= 1 && rgb.B is >= 0 and <= 1) break;
            chroma *= .9;
        }
        static byte Srgb(double value) => (byte)Math.Round(255 * Math.Clamp(value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055, 0, 1));
        return (Srgb(rgb.R), Srgb(rgb.G), Srgb(rgb.B));
    }
}
