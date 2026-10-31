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
    [InlineData(DeviceNoticeState.Unavailable,"실패")]
    [InlineData(DeviceNoticeState.WaitExpired,"실패")]
    public void PendingAndUnknownStepsKeepTheirDisplayState(DeviceNoticeState state,string expected)
    {
        var status=NoticeBubble.Status(new("Mouse",null,"마우스","m",state),false);
        Assert.Equal(expected,status.Text);Assert.NotEqual(Color.FromRgb(0,230,118),((SolidColorBrush)status.Color).Color);
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
