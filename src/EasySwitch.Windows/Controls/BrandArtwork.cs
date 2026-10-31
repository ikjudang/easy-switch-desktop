using System.Windows;
using System.Windows.Media;

namespace EasySwitch.Windows.Controls;

// EasySwitch logo artwork shared by the window rail, splash screen, toasts and floating button.
internal static class BrandArtwork
{
    private static Pen RoundPen(Brush brush, double width) => new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    internal static void Brand(DrawingContext dc,Brush? ink=null)
    {
        // Current Android adaptive foreground, mapped from its 72-unit visible circle to 54 DIP.
        dc.PushTransform(new ScaleTransform(.75, .75));
        dc.PushTransform(new TranslateTransform(-54, -54));
        dc.PushTransform(new TranslateTransform(9.95625, 12.24));
        dc.PushTransform(new ScaleTransform(.163125, .163125));
        ink??=Brushes.White;
        BrandPaths(dc,ink,16);
        dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
    }
    internal static void RailBrand(DrawingContext dc,double size)
    {
        dc.PushTransform(new ScaleTransform(size/512,size/512));dc.PushTransform(new TranslateTransform(-14,0));
        BrandPaths(dc,Brushes.White,20);dc.Pop();dc.Pop();
    }
    internal static void HeaderBrand(DrawingContext dc,double size)
    {
        // macOS header artwork, 5979250: preserve the tile and enlarge only its foreground.
        dc.PushTransform(new TranslateTransform(size/2,size/2));
        dc.PushTransform(new ScaleTransform(size*1.16/512,size*1.16/512));dc.PushTransform(new TranslateTransform(-270,-256));
        BrandPaths(dc,Brushes.White,16);dc.Pop();dc.Pop();dc.Pop();
    }
    private static void BrandPaths(DrawingContext dc,Brush ink,double stroke)
    {
        var pen = RoundPen(ink, stroke);
        foreach (var path in new[] {
            "M125,188 L169,188 A13,13 0 0 1 182,201 L182,311 A13,13 0 0 1 169,324 L125,324 A13,13 0 0 1 112,311 L112,201 A13,13 0 0 1 125,188 Z",
            "M139,311 L155,311", "M341,200 L417,200 A11,11 0 0 1 428,211 L428,263 A11,11 0 0 1 417,274 L341,274 A11,11 0 0 1 330,263 L330,211 A11,11 0 0 1 341,200 Z",
            "M379,274 L379,296", "M356,299 L402,299" }) dc.DrawGeometry(null, pen, Geometry.Parse(path));
        foreach (var path in new[] {
            "M219,228 L269,228 A7,7 0 0 1 276,235 L276,236 A7,7 0 0 1 269,243 L219,243 A7,7 0 0 1 212,236 L212,235 A7,7 0 0 1 219,228 Z",
            "M274,216 L300,235.5 L274,255 Z", "M243,270 L293,270 A7,7 0 0 1 300,277 L300,278 A7,7 0 0 1 293,285 L243,285 A7,7 0 0 1 236,278 L236,277 A7,7 0 0 1 243,270 Z",
            "M238,262 L212,281.5 L238,301 Z" }) dc.DrawGeometry(ink, null, Geometry.Parse(path));
    }
}

internal sealed class RailBrandMark:FrameworkElement
{
    protected override void OnRender(DrawingContext dc)=>BrandArtwork.RailBrand(dc,ActualWidth);
}
internal sealed class HeaderBrandMark:FrameworkElement
{
    protected override void OnRender(DrawingContext dc)=>BrandArtwork.HeaderBrand(dc,ActualWidth);
}
