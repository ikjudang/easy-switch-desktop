using EasySwitch.Core;

namespace EasySwitch.Windows.Tests;

public sealed class SwitchNoticeTests
{
    [Theory]
    [InlineData(DeviceNoticeState.Departed,true)]
    [InlineData(DeviceNoticeState.SignalObserved,true)]
    [InlineData(DeviceNoticeState.Connected,true)]
    [InlineData(DeviceNoticeState.Failed,false)]
    [InlineData(DeviceNoticeState.Unavailable,null)]
    [InlineData(DeviceNoticeState.Cancelled,false)]
    [InlineData(DeviceNoticeState.Waiting,null)]
    [InlineData(DeviceNoticeState.Switching,null)]
    [InlineData(DeviceNoticeState.CommandSent,null)]
    [InlineData(DeviceNoticeState.Unconfirmed,null)]
    public void LocalResultMarksDoNotClaimDestinationConfirmation(DeviceNoticeState state,bool? mark)
    {
        var result=new DeviceResult("Device",null,State:state);
        Assert.Equal(mark,result.Indicator);Assert.Null(result.Success);
    }
    [Fact]
    public void EachDeviceCompletionUpdatesTheCurrentToastWithoutDroppingOtherRows()
    {
        var start=new SwitchNotice("Channel 2",[new("Keyboard",null,"키보드","k",DeviceNoticeState.Switching),new("Mouse",null,"마우스","m",DeviceNoticeState.Waiting)],1);
        var mouse=SwitchNotice.Merge(start,new("Channel 2",[DeviceResult.FromOutcome("m","Mouse","마우스",SwitchOutcome.LocalLinkClosed)],1));
        var complete=SwitchNotice.Merge(mouse,new("Channel 2",[DeviceResult.FromOutcome("k","Keyboard","키보드",SwitchOutcome.TargetReported)],1));
        Assert.Equal(2,complete.Devices.Count);Assert.True(complete.Devices[0].Success);Assert.Null(complete.Devices[1].Success);
        Assert.Equal(DeviceNoticeState.Departed,complete.Devices[1].State);
        var lateStart=SwitchNotice.Merge(complete,new("Channel 2",[new("Mouse",null,"마우스","m",DeviceNoticeState.Switching)],1));
        Assert.Equal(DeviceNoticeState.Departed,lateStart.Devices[1].State);
    }
    [Fact]
    public void OldResultsCannotReplaceANewerChannelOrMergeDevicesByModelName()
    {
        var current=new SwitchNotice("Channel 3",[new("Same model",null,"마우스","m1")],3);
        Assert.Same(current,SwitchNotice.Merge(current,new("Channel 2",[new("Old",true,"마우스","m0")],2)));
        var merged=SwitchNotice.Merge(current,new("Channel 3",[new("Same model",true,"마우스","m2")],3));Assert.Equal(2,merged.Devices.Count);
        var fresh=new SwitchNotice("Channel 1",[new("Keyboard",null,"키보드","k")],4);Assert.Same(fresh,SwitchNotice.Merge(merged,fresh));
    }

    [Fact]
    public void CommandSentAppearsBeforeVerificationWithoutClaimingAnOutcome()
    {
        var sent=DeviceResult.FromProgress("m","Mouse","마우스",new(SwitchStep.Written,1,1,12,12))!;
        var notice=SwitchNotice.Merge(new("Channel 2",[new("Keyboard",null,"키보드","k",DeviceNoticeState.SignalObserved),new("Mouse",null,"마우스","m",DeviceNoticeState.Switching)],1),new("Channel 2",[sent],1));
        Assert.Equal(DeviceNoticeState.SignalObserved,notice.Devices[0].State);
        Assert.Equal(DeviceNoticeState.CommandSent,notice.Devices[1].State);Assert.Null(notice.Devices[1].Indicator);Assert.Null(notice.Devices[1].Success);
        Assert.Null(DeviceResult.FromProgress("m","Mouse","마우스",new(SwitchStep.WriteStarted,1,0,0,0)));
    }

    [Fact]
    public void LatePendingUpdatesCannotUndoCommandDeliveryOrFinalResults()
    {
        var sent=new SwitchNotice("Channel 2",[new("Mouse",null,"마우스","m",DeviceNoticeState.CommandSent)],1);
        foreach(var pending in new[]{DeviceNoticeState.Waiting,DeviceNoticeState.Switching})
            Assert.Equal(DeviceNoticeState.CommandSent,SwitchNotice.Merge(sent,new("Channel 2",[new("Mouse",null,"마우스","m",pending)],1)).Devices[0].State);
        foreach(var outcome in Enum.GetValues<SwitchOutcome>())
        {
            var ended=SwitchNotice.Merge(sent,new("Channel 2",[DeviceResult.FromOutcome("m","Mouse","마우스",outcome)],1));
            var late=SwitchNotice.Merge(ended,sent);Assert.Equal(ended.Devices[0],late.Devices[0]);
        }
    }
}
