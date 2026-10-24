using EasySwitch.Core;

namespace EasySwitch.Core.Tests;

public sealed class FollowPreparationTests
{
    [Fact]
    public void FastRoundTripWaitsForPreviouslySeenMouseAndCanResumeAfterOldFiveSecondLimit()
    {
        const long signal=12000;
        Assert.Equal(FollowReadiness.Wait,FollowPreparation.Readiness(true,true,false,false,3,true));
        Assert.False(FollowPreparation.Expired(signal,signal+6000));
        Assert.Equal(FollowReadiness.Wait,FollowPreparation.Readiness(true,true,true,false,3,true));
        Assert.Equal(FollowReadiness.Ready,FollowPreparation.Readiness(true,true,true,true,3,true));
        Assert.True(FollowPreparation.Expired(signal,signal+10001));
    }
    [Fact]
    public void MouseThatStartedOnAnotherHostIsExcludedRatherThanMovedByOldIntent()
    {
        Assert.Equal(FollowReadiness.Exclude,FollowPreparation.Readiness(true,true,false,false,3,false));
        Assert.Equal(FollowReadiness.Wait,FollowPreparation.Readiness(false,false,null,false,0,false));
        Assert.Equal(FollowReadiness.Ready,FollowPreparation.Readiness(true,true,true,true,3,false));
    }
}
