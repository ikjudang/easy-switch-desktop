using EasySwitch.Core;

namespace EasySwitch.Core.Tests;

// Same decisions as the macOS DeviceEngine late manual wait (f937016).
public sealed class LateDevicePreparationTests
{
    [Fact]
    public void WaitLastsThreeSecondsFromTheRequest()
    {
        const long requested=50_000;
        Assert.Equal(3_000,LateDevicePreparation.WaitMilliseconds);
        Assert.False(LateDevicePreparation.Expired(requested,requested+2_999));
        Assert.True(LateDevicePreparation.Expired(requested,requested+3_000));
    }

    [Fact]
    public void AbsentOrReturningDeviceIsWaitedForOnlyWhileTheSettingIsOn()
    {
        Assert.True(LateDevicePreparation.ShouldWait(true,false,false,null));   // not on this PC yet
        Assert.True(LateDevicePreparation.ShouldWait(true,false,true,false));   // receiver slot whose link is down
        Assert.True(LateDevicePreparation.ShouldWait(true,true,false,null));    // departed earlier and its link already closed
        Assert.False(LateDevicePreparation.ShouldWait(false,false,false,null));
    }

    [Fact]
    public void DeviceLeavingRightNowIsNotWaitedFor()
    {
        Assert.False(LateDevicePreparation.ShouldWait(true,true,true,true));
        Assert.False(LateDevicePreparation.ShouldWait(true,true,true,null));
    }

    [Fact]
    public void ArrivedDeviceIsReadyOnlyAfterItAnsweredOnItsCurrentLink()
    {
        Assert.True(LateDevicePreparation.Ready(true,true,false,9,3,true));
        Assert.False(LateDevicePreparation.Ready(true,true,false,9,3,false)); // link up, no answer yet
        Assert.False(LateDevicePreparation.Ready(true,null,false,0,0,false)); // handle opened, nothing read
        Assert.False(LateDevicePreparation.Ready(true,true,true,9,3,true));   // cache restoration not finished
        Assert.False(LateDevicePreparation.Ready(true,false,false,9,3,true));
        Assert.False(LateDevicePreparation.Ready(false,true,false,9,3,true));
        Assert.False(LateDevicePreparation.Ready(true,true,false,9,0,true));
    }
}
