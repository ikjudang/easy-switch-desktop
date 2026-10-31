namespace EasySwitch.Core;

public static class FloatingAnimationSpeed
{
    public const int Minimum=25, Maximum=300, Default=100;
    public static int Clamp(int percent)=>Math.Clamp(percent,Minimum,Maximum);
    public static double Duration(double milliseconds,int percent)=>milliseconds<=0?0:
        Math.Max(1,Math.Round(milliseconds*100/Clamp(percent),MidpointRounding.AwayFromZero));
}
