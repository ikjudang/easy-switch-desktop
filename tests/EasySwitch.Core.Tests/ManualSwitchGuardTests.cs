using EasySwitch.Core;

namespace EasySwitch.Core.Tests;

public sealed class ManualSwitchGuardTests
{
    [Fact]
    public void KeyboardReturnMakesAnImmediateNewDepartureEligible()
    {
        var guard=new ManualSwitchGuard();guard.Begin(10,1);
        Assert.True(guard.Matches(10,1));
        // Returning keyboard invalidates batch 10. A physical departure to the same channel,
        // even within 2 seconds, must not be swallowed as the earlier manual request's echo.
        Assert.False(guard.Matches(11,1));
    }
    [Fact]
    public void CompletedRequestDoesNotSuppressAnotherPhysicalSwitch()
    {
        var guard=new ManualSwitchGuard();guard.Begin(10,1);guard.Complete(10);
        Assert.False(guard.Matches(10,1));
    }
    [Fact]
    public void LateCompletionCannotClearANewerManualRequest()
    {
        var guard=new ManualSwitchGuard();guard.Begin(10,1);guard.Begin(11,2);guard.Complete(10);
        Assert.True(guard.Matches(11,2));Assert.False(guard.Matches(11,1));guard.Complete(11);Assert.False(guard.Matches(11,2));
    }
}
