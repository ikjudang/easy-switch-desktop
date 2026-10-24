using System.IO;
using System.Text;
using EasySwitch.Hidpp;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class StartupDiscoveryTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
    private long _now;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitiallySilentKeyboardIsFoundWithoutSwitchingChannelsOrReceivingAnArrivalNotice(bool receiver)
    {
        using var log=new AppLog(_directory);var transport=new KeyboardTransport{IsReceiver=receiver};
        transport.Lookup=call=>Task.FromResult<byte[]?>(call==1?null:transport.LookupReply());
        var device=new HostDevice(transport,1,log,tickCount:()=>_now);
        await device.RefreshAsync(CancellationToken.None);Assert.False(device.Info.Online);Assert.Equal(1,transport.Lookups);
        _now=99;await device.RefreshAsync(CancellationToken.None);Assert.Equal(1,transport.Lookups);
        _now=100;await device.RefreshAsync(CancellationToken.None);
        Assert.True(device.Info.Online);Assert.Equal("키보드",device.Info.Kind);Assert.Equal("Keyboard",device.Info.Name);Assert.Equal(0,device.CurrentHost);Assert.Equal(2,transport.Lookups);
        Assert.Equal(0,transport.ChannelWrites);
    }

    [Fact]
    public async Task SilentReceiverDeviceStillRetriesAfterTheInitialWarmup()
    {
        using var log=new AppLog(_directory);var transport=new KeyboardTransport();
        transport.Lookup=call=>Task.FromResult<byte[]?>(call==1?null:transport.LookupReply());
        var device=new HostDevice(transport,1,log,tickCount:()=>_now);
        _now=16_000;await device.RefreshAsync(CancellationToken.None);
        _now=18_999;await device.RefreshAsync(CancellationToken.None);Assert.Equal(1,transport.Lookups);
        _now=19_000;await device.RefreshAsync(CancellationToken.None);Assert.True(device.Info.Online);Assert.Equal(2,transport.Lookups);
    }

    [Fact]
    public async Task ExplicitEmptySlotStaysSlowButManualRefreshBypassesItsCooldown()
    {
        using var log=new AppLog(_directory);var transport=new KeyboardTransport();
        transport.Lookup=call=>Task.FromResult<byte[]?>(call==1?Hex.Parse("10 01 8F 00 09 09 00"):transport.LookupReply());
        var device=new HostDevice(transport,1,log,tickCount:()=>_now);
        await device.RefreshAsync(CancellationToken.None);
        _now=29999;await device.RefreshAsync(CancellationToken.None);Assert.Equal(1,transport.Lookups);
        await device.RefreshAsync(CancellationToken.None,true);Assert.True(device.Info.Online);Assert.Equal(2,transport.Lookups);
    }

    [Fact]
    public async Task ForcedArrivalRefreshIsRetainedWhenTheInitialQueryIsStillRunning()
    {
        using var log=new AppLog(_directory);var transport=new KeyboardTransport();
        var firstReply=new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Lookup=call=>call==1?firstReply.Task:Task.FromResult<byte[]?>(transport.LookupReply());
        var device=new HostDevice(transport,1,log,tickCount:()=>_now);
        var pending=device.RefreshAsync(CancellationToken.None);Assert.False(pending.IsCompleted);
        device.ApplyConnectionNotice(ReceiverMessages.ParseDeviceConnection(Hex.Parse("10 01 41 10 01 66 B3"))!);
        await device.RefreshAsync(CancellationToken.None,true);firstReply.SetResult(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2,transport.Lookups);Assert.True(device.Info.Online);Assert.Equal(0,_now);Assert.Equal(0,transport.ChannelWrites);
    }

    [Fact]
    public async Task ReadyDeviceKeepsTheNormalThreeSecondInterval()
    {
        using var log=new AppLog(_directory);var transport=new KeyboardTransport();
        var device=new HostDevice(transport,1,log,tickCount:()=>_now);
        await device.RefreshAsync(CancellationToken.None);Assert.True(device.Info.Online);
        _now=2999;await device.RefreshAsync(CancellationToken.None);Assert.Equal(1,transport.Lookups);
        _now=3000;await device.RefreshAsync(CancellationToken.None);Assert.Equal(2,transport.Lookups);
    }

    private sealed class KeyboardTransport:IDeviceTransport
    {
        public string Key=>"fixture-keyboard";
        public string Name=>"Fixture receiver";
        public string ConnectionLabel=>IsReceiver?"Logi Bolt":"Bluetooth";
        public bool IsReceiver {get;init;}=true;
        public bool IsOpen=>true;
        public int SoftwareId=>9;
        public int Lookups {get;private set;}
        public int ChannelWrites {get;private set;}
        public Func<int,Task<byte[]?>>? Lookup {get;set;}
        public byte[] LookupReply()=>HidppFrame.Build(0,0,deviceIndex:1,softwareId:9,parameters:[9,0,0]).ToBytes();
        public async Task<byte[]?> RequestAsync(byte[] raw,CancellationToken cancel)
        {
            var request=HidppFrameParser.Parse(raw)!;
            if(request.FeatureIndex==0 && request.Param(0)==0x18 && request.Param(1)==0x14)
            {Lookups++;return Lookup is null?LookupReply():await Lookup(Lookups);}
            byte[] parameters=(request.FeatureIndex,request.FunctionId) switch
            {
                (0,0) when request.Param(1)==5=>[10,0,0],
                (9,0)=>[3,0,0],
                (10,2)=>[0,0,0],
                (10,0)=>[8,0,0],
                (10,1)=>Encoding.UTF8.GetBytes("Keyboard").Skip(request.Param(0)).ToArray(),
                _=>throw new InvalidOperationException($"Unexpected fixture query: {request}"),
            };
            return HidppFrame.Build(request.FeatureIndex,request.FunctionId,parameters.Length>3?ReportType.Long:ReportType.Short,request.DeviceIndex,request.SoftwareId,parameters).ToBytes();
        }
        public Task SendAsync(byte[] request,CancellationToken cancel)
        {ChannelWrites++;throw new InvalidOperationException("Discovery must not send a channel command");}
    }
    public void Dispose()
    {
        var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"EasySwitch.Tests"))+Path.DirectorySeparatorChar;
        var target=Path.GetFullPath(_directory);
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe fixture cleanup");
        if(Directory.Exists(target))Directory.Delete(target,true);
    }
}
