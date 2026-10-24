namespace EasySwitch.Windows.Infrastructure;

internal static class AppVersion
{
    public static Version Current { get; } = typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    public static string Label => Current.ToString(3);
}
