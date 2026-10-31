using System.IO;
using EasySwitch.Hidpp;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class DeviceReadinessTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
    private long _now;

    [Fact]
    public async Task ClosedTransportCannotKeepItsLastSuccessfulOnlineStateWhileTheCloseCallbackIsDelayed()
    {
        using var log=new AppLog(_root);var wire=new KeyboardTransport();
        var keyboard=new HostDevice(wire,1,log,tickCount:()=>_now);
        await keyboard.RefreshAsync(default);Assert.True(keyboard.Info.Online);
        wire.IsOpen=false;
        Assert.False(keyboard.Info.Online);
        Assert.Equal(0,wire.ChannelWrites);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArrivalCallbackCannotPublishAHostReplyAfterItsLinkWasInvalidated(bool departed)
    {
        using var log=new AppLog(_root);var wire=new KeyboardTransport();
        var keyboard=new HostDevice(wire,1,log,tickCount:()=>_now);
        keyboard.LinkArrived+=_=>{if(departed)keyboard.NoteDepartureSignal();else keyboard.MarkLinkClosed();};
        Assert.False(keyboard.ReportLocalHost(new(3,0),keyboard.HostQueryRevision));
        Assert.False(keyboard.Info.Online);
        Assert.Equal(0,wire.ChannelWrites);
    }

    [Fact]
    public void HostReplyFromAClosedTransportDoesNotCreateAnArrival()
    {
        using var log=new AppLog(_root);var wire=new KeyboardTransport{IsOpen=false};
        var keyboard=new HostDevice(wire,1,log,tickCount:()=>_now);var arrivals=0;
        keyboard.LinkArrived+=_=>arrivals++;
        Assert.False(keyboard.ReportLocalHost(new(3,0),keyboard.HostQueryRevision));
        Assert.False(keyboard.Info.Online);Assert.Equal(0,arrivals);
    }

    [Fact]
    public async Task PersistentControlQueryBusyClearsReadinessAndOrdinaryPollingRecoversWithoutReselection()
    {
        using var log=new AppLog(_root);var wire=new KeyboardTransport();
        var keyboard=new HostDevice(wire,1,log,tickCount:()=>_now);
        await keyboard.RefreshAsync(default);Assert.True(keyboard.Info.Online);
        var generation=keyboard.ConnectionGeneration;var arrivals=0;keyboard.LinkArrived+=_=>arrivals++;
        wire.ControlBusy=true;await keyboard.RefreshAsync(default,true);
        Assert.False(keyboard.Info.Online);
        Assert.True(keyboard.LocalLinkConnected);Assert.True(wire.IsOpen);
        Assert.Equal(generation,keyboard.ConnectionGeneration);
        wire.ControlBusy=false;_now=100;await keyboard.RefreshAsync(default);
        Assert.True(keyboard.Info.Online);Assert.Equal(0,arrivals);
        Assert.Equal(generation,keyboard.ConnectionGeneration);Assert.Equal(0,wire.ChannelWrites);
    }

    [Fact]
    public async Task OptionalNameQueryBusyDoesNotHideAFreshSuccessfulControlQuery()
    {
        using var log=new AppLog(_root);var wire=new KeyboardTransport{NameBusy=true};
        var keyboard=new HostDevice(wire,1,log,tickCount:()=>_now);
        await keyboard.RefreshAsync(default);
        Assert.True(keyboard.Info.Online);Assert.True(keyboard.LocalLinkConnected);
        Assert.Equal(0,wire.ChannelWrites);
    }

    private sealed class KeyboardTransport:IDeviceTransport
    {
        public string Key=>"readiness-keyboard";public string Name=>"Fixture keyboard";
        public string ConnectionLabel=>"Logi Bolt";public bool IsReceiver=>true;
        public bool IsOpen{get;set;}=true;public int SoftwareId=>9;
        public bool ControlBusy,NameBusy;public int ChannelWrites;
        public Task<byte[]?> RequestAsync(byte[] raw,CancellationToken cancel)
        {
            var request=HidppFrameParser.Parse(raw)!;
            var control=request.FeatureIndex==0 && request.Param(0)==0x18;
            if(control && ControlBusy || request.FeatureIndex==0 && request.Param(1)==5 && NameBusy)
                return Task.FromResult<byte[]?>([0x10,1,0xFF,(byte)request.FeatureIndex,(byte)(request.FunctionId<<4|request.SoftwareId),0x08,0]);
            byte[] parameters=(request.FeatureIndex,request.FunctionId) switch
            {
                (0,0) when control=>[9,0,0],
                (9,0)=>[3,0,0],
                (0,0) when request.Param(1)==5=>[0,0,0],
                _=>throw new InvalidOperationException("Unexpected readiness fixture query"),
            };
            return Task.FromResult<byte[]?>(HidppFrame.Build(request.FeatureIndex,request.FunctionId,deviceIndex:1,softwareId:9,parameters:parameters).ToBytes());
        }
        public Task SendAsync(byte[] request,CancellationToken cancel)
        {ChannelWrites++;throw new InvalidOperationException("Readiness checks must not switch channels");}
    }
    public void Dispose()
    {
        var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"EasySwitch.Tests"))+Path.DirectorySeparatorChar;
        var target=Path.GetFullPath(_root);
        if(!target.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe fixture cleanup");
        if(Directory.Exists(target))Directory.Delete(target,true);
    }
}
