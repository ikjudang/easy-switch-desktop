namespace EasySwitch.Windows.Tests;

// Tests that show real windows on the desktop (and reorder them against other windows) run only
// when EASYSWITCH_DESKTOP_TESTS=1 is set, so a normal test run never flashes windows on screen.
public sealed class DesktopFactAttribute : FactAttribute
{
    public const string Variable = "EASYSWITCH_DESKTOP_TESTS";
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Shows windows on the desktop. Set {Variable}=1 to run it.";
    }
}
