namespace EasySwitch.Core;

public static class LanguageMenuPlacement
{
    public static FloatPoint Place(FloatBounds anchor,FloatBounds workArea,double width,double height)
    {
        var left=workArea.Left+8;var right=workArea.Right-8;var top=workArea.Top+8;var bottom=workArea.Bottom-8;
        var x=anchor.Left;if(x+width>right)x=anchor.Right-width;
        x=Math.Clamp(x,left,Math.Max(left,right-width));
        var y=anchor.Bottom+2;if(y+height>bottom)y=anchor.Top-height-2;
        return new(x,Math.Clamp(y,top,Math.Max(top,bottom-height)));
    }
}
