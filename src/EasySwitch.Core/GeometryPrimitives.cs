namespace EasySwitch.Core;

// Plain screen-space values shared by menu placement and the floating button geometry.
public readonly record struct FloatPoint(double X, double Y)
{
    public static FloatPoint operator +(FloatPoint a, FloatPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static FloatPoint operator -(FloatPoint a, FloatPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static FloatPoint operator *(FloatPoint a, double n) => new(a.X * n, a.Y * n);
    public double Length => Math.Sqrt(X * X + Y * Y);
}
public readonly record struct FloatBounds(double Left, double Top, double Right, double Bottom);
