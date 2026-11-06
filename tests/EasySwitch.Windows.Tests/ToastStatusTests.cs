using System.Windows.Media;
using EasySwitch.Windows.Notices;

namespace EasySwitch.Windows.Tests;

public sealed class ToastStatusTests
{
    [Theory]
    [InlineData(DeviceNoticeState.Waiting,"전환 중")]
    [InlineData(DeviceNoticeState.Switching,"전환 중")]
    [InlineData(DeviceNoticeState.CommandSent,"전환 중")]
    [InlineData(DeviceNoticeState.Unconfirmed,"확인 필요")]
    [InlineData(DeviceNoticeState.Cancelled,"확인 필요")]
    [InlineData(DeviceNoticeState.Failed,"실패")]
    [InlineData(DeviceNoticeState.Unavailable,"확인 필요")]
    [InlineData(DeviceNoticeState.WaitExpired,"확인 필요")]
    public void PendingAndUnknownStepsKeepTheirDisplayState(DeviceNoticeState state,string expected)
    {
        var status=NoticeBubble.Status(new("Mouse",null,"마우스","m",state),false);
        Assert.Equal(expected,status.Text);Assert.NotEqual(Color.FromRgb(0,230,118),((SolidColorBrush)status.Color).Color);
    }
    // 2026-11-01 14:38~14:40: the mouse had left this PC for the tablet, the 10 s wait expired without any
    // command, and the toast said Failed while the mouse sat on the destination. Not reaching a device is
    // not a failed switch: only a sent command that did not take effect is Failed (red).
    [Theory]
    [InlineData(DeviceNoticeState.WaitExpired)]
    [InlineData(DeviceNoticeState.Unavailable)]
    public void ADeviceThisPcCouldNotReachIsUnconfirmedInWhiteNeverFailedInRed(DeviceNoticeState state)
    {
        foreach(var english in new[]{false,true})
        {
            var status=NoticeBubble.Status(new("Mouse",null,"마우스","m",state),english);
            Assert.Equal(english?"Unconfirmed":"확인 필요",status.Text);Assert.Equal(Colors.White,((SolidColorBrush)status.Color).Color);
        }
        var sentButStayed=NoticeBubble.Status(new("Mouse",null,"마우스","m",DeviceNoticeState.Failed),false);
        Assert.Equal("실패",sentButStayed.Text);Assert.Equal(Color.FromRgb(229,57,53),((SolidColorBrush)sentButStayed.Color).Color);
    }
    [Theory]
    [InlineData(DeviceNoticeState.SignalObserved)]
    [InlineData(DeviceNoticeState.Departed)]
    public void CompletedLocalStepIsShownWithoutChangingDestinationOutcome(DeviceNoticeState state)
    {
        var row=new DeviceResult("Mouse",null,"마우스","m",state);
        var status=NoticeBubble.Status(row,false);
        Assert.Equal("완료",status.Text);Assert.Equal(Color.FromRgb(0,230,118),((SolidColorBrush)status.Color).Color);
        Assert.Null(row.Success);Assert.Equal(state,row.State);
    }
    [Fact]
    public void ConnectionUsesPlainWhiteAndConfirmedUsesGreenText()
    {
        var connected=NoticeBubble.Status(new("Keyboard",null,"키보드","k",DeviceNoticeState.Connected),false);
        Assert.Equal("연결됨",connected.Text);Assert.Equal(Colors.White,((SolidColorBrush)connected.Color).Color);
        var confirmed=NoticeBubble.Status(new("Mouse",true,"마우스","m",DeviceNoticeState.Confirmed),false);
        Assert.Equal("완료",confirmed.Text);Assert.Equal(Color.FromRgb(0,230,118),((SolidColorBrush)confirmed.Color).Color);
        var failed=NoticeBubble.Status(new("Mouse",false),true);
        Assert.Equal("Failed",failed.Text);Assert.Equal(Color.FromRgb(229,57,53),((SolidColorBrush)failed.Color).Color);
    }
}
