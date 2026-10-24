using EasySwitch.Windows.Hardware;

namespace EasySwitch.Windows.Tests;

public sealed class LocalConnectionNoticesTests
{
    [Theory]
    [InlineData("키보드")]
    [InlineData("마우스")]
    public void InitialConnectionAndRealReturnsNotifyButPollingDoesNot(string kind)
    {
        var notices=new LocalConnectionNotices();var connection=new object();
        var device=new DeviceInfo("id","Device","Logi Bolt",kind,true,3,0,9);
        var first=notices.Observe(device,connection,1);Assert.NotNull(first);Assert.Equal(DeviceNoticeState.Connected,first.State);Assert.True(first.Indicator);Assert.Null(first.Success);
        Assert.Null(notices.Observe(device,connection,1));
        Assert.Null(notices.Observe(device with{Online=false},connection,1));
        Assert.Null(notices.Observe(device,connection,1)); // Query timeout recovery is not a new wireless connection.
        Assert.NotNull(notices.Observe(device,connection,2)); // Bolt/Unifying 0x41 return.
        Assert.NotNull(notices.Observe(device,new object(),1)); // Bluetooth HID handle reopened.
    }

    [Fact]
    public void UnconfirmedDiscoveryWaitsForHostAndKeyboardMouseMetadata()
    {
        var notices=new LocalConnectionNotices();var connection=new object();
        var device=new DeviceInfo("id","Receiver slot","Logi Bolt","기기",false,0,null,0);
        Assert.Null(notices.Observe(device,connection,1));
        Assert.Null(notices.Observe(device with{Online=true,CurrentHost=0},connection,1));
        Assert.Null(notices.Observe(device with{Online=true,Kind="키보드"},connection,1));
        Assert.NotNull(notices.Observe(device with{Online=true,Kind="키보드",CurrentHost=0,HostCount=3,Name="Keyboard"},connection,1));
    }
}
