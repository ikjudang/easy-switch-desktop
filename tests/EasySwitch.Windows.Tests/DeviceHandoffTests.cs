using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EasySwitch.Hidpp;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

public sealed class DeviceHandoffTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
    private string CacheDirectory=>Path.Combine(_root,"engine");
    private long _now;

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task RestartRestoresHintsButRequiresFreshIdentityFeatureAndHostBeforeSending(bool receiver)
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var old=new Wire("keyboard",receiver);
        var id=await Seed(old,log);
        using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(CacheDirectory,"device-routes.json"))))
        {
            var route=json.RootElement.GetProperty("Routes")[0];
            Assert.False(route.TryGetProperty("Online",out _));Assert.False(route.TryGetProperty("CurrentHost",out _));
            Assert.Equal(old.Key,route.GetProperty("TransportKey").GetString());
        }
        var current=new Wire(old.Key,receiver){Feature=11,Host=1,MetadataHold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var monitor=new DeviceMonitor(log,CacheDirectory);monitor.ExpectDevices([id]);
        var device=monitor.RegisterDevice(current,current.Index,()=>_now);
        Assert.Equal(id,device.Id);Assert.False(device.Info.Online);Assert.Equal(0,device.Info.FeatureIndex);Assert.Null(device.CurrentHost);
        Assert.Equal("Keyboard",device.Name);Assert.Equal(DeviceRestoreStage.Checking,monitor.Restorations.Single().Stage);
        var refresh=device.RefreshAsync(default);
        await current.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(device.Info.Online);Assert.True(device.Restoring);
        await Assert.ThrowsAsync<IOException>(()=>device.SetHostAsync(2,default));Assert.Equal(0,current.Writes);
        current.MetadataHold.SetResult();await refresh;
        Assert.True(device.Info.Online);Assert.False(device.Restoring);Assert.Equal(11,device.Info.FeatureIndex);Assert.Equal(1,device.CurrentHost);
        Assert.Equal(DeviceRestoreStage.Ready,monitor.Restorations.Single().Stage);
        await device.SetHostAsync(2,default);Assert.Equal(1,current.Writes);Assert.Equal(11,current.WrittenFeature);
    }

    [Theory]
    [InlineData("silent")] [InlineData("busy")] [InlineData("metadata")]
    public async Task CachedKeyboardRecoversThroughOrdinaryPollingWithoutReselection(string failure)
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var id=await Seed(new Wire("keyboard"),log);
        var wire=new Wire("keyboard"){Silent=failure=="silent",Busy=failure=="busy",MetadataSilent=failure=="metadata"};
        await using var monitor=new DeviceMonitor(log,CacheDirectory);monitor.ExpectDevices([id]);
        var keyboard=monitor.RegisterDevice(wire,wire.Index,()=>_now);
        await keyboard.RefreshAsync(default);Assert.False(keyboard.Info.Online);Assert.True(keyboard.Restoring);
        Assert.Equal(DeviceRestoreStage.Retrying,monitor.Restorations.Single().Stage);
        wire.Silent=false;wire.Busy=false;wire.MetadataSilent=false;_now=100;
        await keyboard.RefreshAsync(default);
        Assert.True(keyboard.Info.Online);Assert.Equal(DeviceRestoreStage.Ready,monitor.Restorations.Single().Stage);
        Assert.Equal(id,keyboard.Id);Assert.Equal(0,wire.Writes);
    }

    [Fact]
    public async Task SelectedReceiverSlotDoesNotWaitForTheEmptySlotCooldownAtStartup()
    {
        using var log=new AppLog(_root);var wire=new Wire("known-selected"){AbsentOnce=true};
        var device=new HostDevice(wire,wire.Index,log,tickCount:()=>_now);device.ExpectRestore(true);
        await device.RefreshAsync(default);Assert.False(device.Info.Online);Assert.Equal(1,wire.Lookups);
        _now=99;await device.RefreshAsync(default);Assert.Equal(1,wire.Lookups);
        _now=100;await device.RefreshAsync(default);Assert.True(device.Info.Online);Assert.Equal(2,wire.Lookups);
        Assert.Equal(0,wire.Writes);
    }

    [Fact]
    public async Task ChangedDeviceKindInvalidatesTheOldCacheAndNeverUsesItsFeatureAddress()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var id=await Seed(new Wire("keyboard"),log);
        var wire=new Wire("keyboard"){Kind=3,Name="Mouse",Feature=12};
        await using(var monitor=new DeviceMonitor(log,CacheDirectory))
        {
            var invalidated=0;monitor.PairingInvalidated+=_=>invalidated++;monitor.ExpectDevices([id]);
            var device=monitor.RegisterDevice(wire,wire.Index,()=>_now);await device.RefreshAsync(default);
            Assert.Equal(1,invalidated);Assert.False(device.Info.Online);Assert.Equal(0,device.Info.FeatureIndex);Assert.Equal(0,wire.Writes);
        }
        Assert.Null(new DeviceRouteCache(CacheDirectory,log).Find(id));
    }

    [Fact]
    public async Task RenamingTheSameKindDoesNotInvalidateTheUsersSelection()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var id=await Seed(new Wire("keyboard"),log);
        var wire=new Wire("keyboard"){Name="Renamed keyboard"};
        await using(var monitor=new DeviceMonitor(log,CacheDirectory))
        {
            monitor.ExpectDevices([id]);monitor.PairingInvalidated+=_=>throw new InvalidOperationException("A name change is not proof of re-pairing");
            var device=monitor.RegisterDevice(wire,wire.Index,()=>_now);await device.RefreshAsync(default);
            Assert.True(device.Info.Online);Assert.Equal("Renamed keyboard",device.Name);
        }
        Assert.Equal("Renamed keyboard",new DeviceRouteCache(CacheDirectory,log).Find(id)!.Name);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":2,\"Routes\":[]}")]
    [InlineData("{\"Version\":1,\"Routes\":[{\"Id\":null}]}")]
    public async Task InvalidCacheFallsBackToDiscoveryAndDoesNotAlterSavedSelection(string content)
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));Directory.CreateDirectory(CacheDirectory);
        var wire=new Wire("keyboard");var id=new HostDevice(wire,wire.Index,log).Id;
        var store=new SettingsStore(CacheDirectory);store.Save(new(){SelectedDeviceIds=[id],SelectedDeviceNames=[new(id,"Keyboard","키보드")]});
        File.WriteAllText(Path.Combine(CacheDirectory,"device-routes.json"),content);
        await using(var monitor=new DeviceMonitor(log,CacheDirectory))
        {
            monitor.ExpectDevices([id]);var device=monitor.RegisterDevice(wire,wire.Index,()=>_now);
            Assert.False(device.Restoring);await device.RefreshAsync(default);Assert.True(device.Info.Online);
        }
        Assert.Equal(new[]{id},new SettingsStore(CacheDirectory).Value.SelectedDeviceIds);
    }

    [Fact]
    public async Task OlderServiceStateIsHandedOffAsHintsAndIsRevalidatedInTheNewService()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var wire=new Wire("keyboard");
        var id=new HostDevice(wire,wire.Index,log).Id;
        var name="EasySwitch.OldHandoff."+Guid.NewGuid().ToString("N");
        await using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var serve=Task.Run(async()=>
        {
            await server.WaitForConnectionAsync();var request=await ServiceWire.ReadAsync<ServiceRequest>(server,default);
            Assert.Equal("State",request.Command);
            await ServiceWire.WriteAsync(server,new ServiceReply(new("old-service",1,[new(id,"Keyboard","Logi Bolt","키보드",true,3,2,17)],EnginePreferences.From(new()),null,"fixture",[])),default);
        });
        var captured=await ServiceDeviceHandoff.CaptureAsync(name,Environment.ProcessId);await serve;
        await ServiceDeviceHandoff.ImportAsync(CacheDirectory,captured,log);
        await using var monitor=new DeviceMonitor(log,CacheDirectory);monitor.ExpectDevices([id]);
        var restored=monitor.RegisterDevice(wire,wire.Index,()=>_now);
        Assert.True(restored.Restoring);Assert.False(restored.Info.Online);Assert.Null(restored.CurrentHost);
        await restored.RefreshAsync(default);Assert.True(restored.Info.Online);Assert.Equal(9,restored.Info.FeatureIndex);Assert.Equal(0,restored.CurrentHost);
        Assert.Equal(DeviceRestoreStage.Ready,monitor.Restorations.Single().Stage);Assert.Equal(0,wire.Writes);
    }

    [Fact]
    public async Task HandoffNeverOverwritesAnExistingDurableRouteWithOlderSnapshotData()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var id=await Seed(new Wire("keyboard"){Feature=14},log);
        await ServiceDeviceHandoff.ImportAsync(CacheDirectory,[new(id,"Older name","Logi Bolt","키보드",true,3,2,9)],log);
        var route=new DeviceRouteCache(CacheDirectory,log).Find(id)!;
        Assert.Equal(14,route.FeatureIndex);Assert.Equal("Keyboard",route.Name);
    }

    [Fact]
    public async Task ResponsesFromAnOlderTransportCannotChangeTheNewTransportRestorationState()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));await using var monitor=new DeviceMonitor(log,CacheDirectory);
        var oldWire=new Wire("keyboard");var old=monitor.RegisterDevice(oldWire,oldWire.Index,()=>_now);
        monitor.ExpectDevices([old.Id]);await old.RefreshAsync(default);old.MarkLinkClosed();
        var currentWire=new Wire("keyboard");var current=monitor.RegisterDevice(currentWire,currentWire.Index,()=>_now);
        await current.RefreshAsync(default);Assert.True(current.Info.Online);
        old.ReportLocalHost(new(3,0),old.HostQueryRevision);old.MarkLinkClosed();
        Assert.True(current.Info.Online);Assert.Equal(DeviceRestoreStage.Ready,monitor.Restorations.Single().Stage);
    }

    private async Task<string> Seed(Wire wire,AppLog log)
    {
        await using var monitor=new DeviceMonitor(log,CacheDirectory);var device=monitor.RegisterDevice(wire,wire.Index,()=>_now);
        monitor.ExpectDevices([device.Id]);await device.RefreshAsync(default);Assert.True(device.Info.Online);Assert.True(device.IdentityReady);
        return device.Id;
    }

    [Fact]
    public async Task ReceiverIdentityChangeInvalidatesThePersistedRouteBeforeItCanBeRestored()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var wire=new Wire("keyboard");var id=await Seed(wire,log);
        var cache=new DeviceRouteCache(CacheDirectory,log);
        cache.Remember(new(id,"Keyboard","Logi Bolt","키보드",true,3,0,9),0xB366,wire.Key,wire.Index);await cache.FlushAsync();
        await using(var monitor=new DeviceMonitor(log,CacheDirectory))
        {
            monitor.ExpectDevices([id]);var invalidated=0;monitor.PairingInvalidated+=_=>invalidated++;
            var device=monitor.RegisterDevice(new Wire("keyboard"),1,()=>_now);Assert.True(device.Restoring);
            device.ApplyConnectionNotice(ReceiverMessages.ParseDeviceConnection(Hex.Parse("10 01 41 10 01 42 B0"))!);
            Assert.Equal(1,invalidated);Assert.False(device.Info.Online);Assert.Equal(0,device.Info.FeatureIndex);
        }
        Assert.Null(new DeviceRouteCache(CacheDirectory,log).Find(id));
    }

    [Fact]
    public async Task AggregateCompletionRequiresEverySelectedDeviceToBeConfirmed()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var key=new Wire("keyboard");var mouse=new Wire("mouse"){Name="Mouse",Kind=3};
        await using var monitor=new DeviceMonitor(log,CacheDirectory);
        var keyboard=monitor.RegisterDevice(key,key.Index,()=>_now);var pointer=monitor.RegisterDevice(mouse,mouse.Index,()=>_now);
        monitor.ExpectDevices([keyboard.Id,pointer.Id]);await keyboard.RefreshAsync(default);
        Assert.Equal(1,monitor.Restorations.Count(state=>state.Stage==DeviceRestoreStage.Ready));
        Assert.DoesNotContain(log.Recent(),entry=>entry.Message.Contains("복원 완료: 2/2"));
        await pointer.RefreshAsync(default);Assert.All(monitor.Restorations,state=>Assert.Equal(DeviceRestoreStage.Ready,state.Stage));
        Assert.Contains(log.Recent(),entry=>entry.Message.Contains("복원 완료: 2/2"));
    }

    [Fact]
    public async Task SameNameOnAnotherTransportIsNeverMergedIntoTheSavedDevice()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));var id=await Seed(new Wire("old-path"),log);
        await using var monitor=new DeviceMonitor(log,CacheDirectory);monitor.ExpectDevices([id]);
        var wire=new Wire("other-path");var other=monitor.RegisterDevice(wire,wire.Index,()=>_now);
        Assert.NotEqual(id,other.Id);Assert.False(other.Restoring);await other.RefreshAsync(default);
        Assert.True(other.Info.Online);Assert.Equal(DeviceRestoreStage.WaitingForTransport,monitor.Restorations.Single().Stage);
        Assert.Equal(0,wire.Writes);
    }

    [Fact]
    public async Task FailedCacheWriteKeepsThePreviousFileAndRetriesTheSameRouteAfterStorageRecovers()
    {
        using var log=new AppLog(Path.Combine(_root,"logs"));Directory.CreateDirectory(CacheDirectory);
        var path=Path.Combine(CacheDirectory,"device-routes.json");File.WriteAllText(path,"{\"Version\":1,\"Routes\":[]}");
        var cache=new DeviceRouteCache(CacheDirectory,log);var wire=new Wire("keyboard");
        var id=new HostDevice(wire,wire.Index,log).Id;var info=new DeviceInfo(id,"Keyboard","Logi Bolt","키보드",true,3,0,9);
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
        {cache.Remember(info,null,wire.Key,wire.Index);await cache.FlushAsync();}
        Assert.Null(new DeviceRouteCache(CacheDirectory,log).Find(id));
        cache.Remember(info,null,wire.Key,wire.Index);await cache.FlushAsync();
        Assert.NotNull(new DeviceRouteCache(CacheDirectory,log).Find(id));
        Assert.Contains(log.Recent(),entry=>entry.Message.StartsWith("기기 복원 캐시 저장 실패:"));
    }
    private sealed class Wire(string key,bool receiver=true):IDeviceTransport
    {
        public string Key=>key;public string Name{get;set;}="Keyboard";public bool IsReceiver=>receiver;
        public string ConnectionLabel=>receiver?"Logi Bolt":"Bluetooth";public int Index=>receiver?1:255;
        public bool IsOpen=>true;public int SoftwareId=>9;public int Feature=9,Host,Kind,Lookups,Writes,WrittenFeature;
        public bool Silent,Busy,MetadataSilent,AbsentOnce;
        public TaskCompletionSource? MetadataHold;
        public TaskCompletionSource MetadataEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]?> RequestAsync(byte[] raw,CancellationToken cancel)
        {
            var request=HidppFrameParser.Parse(raw)!;
            if(request.FeatureIndex==0 && request.Param(0)==0x18)
            {
                Lookups++;if(Silent)return null;
                if(AbsentOnce && Lookups==1)return [0x10,(byte)Index,0x8F,0,9,9,0];
                if(Busy)return [0x10,(byte)Index,0xFF,0,9,8,0];
                return Reply(request,[(byte)Feature,0,0]);
            }
            if(request.FeatureIndex==Feature)return Reply(request,[3,(byte)Host,0]);
            if(request.FeatureIndex==0 && request.Param(1)==5)
            {
                MetadataEntered.TrySetResult();if(MetadataHold is not null)await MetadataHold.Task.WaitAsync(cancel);
                return MetadataSilent?null:Reply(request,[20,0,0]);
            }
            byte[] parameters=(request.FeatureIndex,request.FunctionId) switch
            {
                (20,2)=>[(byte)Kind,0,0],
                (20,0)=>[(byte)Encoding.UTF8.GetByteCount(Name),0,0],
                (20,1)=>Encoding.UTF8.GetBytes(Name).Skip(request.Param(0)).Take(16).ToArray(),
                _=>throw new InvalidOperationException("Unexpected handoff query: "+request),
            };
            return Reply(request,parameters);
        }
        private byte[] Reply(HidppFrame request,byte[] parameters)=>HidppFrame.Build(request.FeatureIndex,request.FunctionId,parameters.Length>3?ReportType.Long:ReportType.Short,Index,9,parameters).ToBytes();
        public Task SendAsync(byte[] request,CancellationToken cancel)
        {Writes++;WrittenFeature=request[2];return Task.CompletedTask;}
    }
    public void Dispose()
    {
        var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"EasySwitch.Tests"))+Path.DirectorySeparatorChar;
        var target=Path.GetFullPath(_root);if(!target.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe handoff fixture cleanup");
        if(Directory.Exists(target))Directory.Delete(target,true);
    }
}
