using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using EasySwitch.Core;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class TransportRecoveryTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    [Fact]
    public async Task StalledOutputInvalidatesTheTransportAndItsVerificationGeneration()
    {
        var root=Temp();using var log=new AppLog(root);var stream=new StalledStream(Path.Combine(root,"hid.bin"));
        await using var connection=new HidConnection("stalled",[(Info(stream.Name),stream)],log,TimeSpan.FromMilliseconds(40));
        var mouse=Device(connection,log,"Mouse","마우스");var generation=mouse.ConnectionGeneration;
        await Assert.ThrowsAsync<IOException>(()=>connection.SendAsync(RootFeature.BuildGetFeature(0x1814).ToBytes(),default));
        Assert.False(connection.IsOpen);Assert.True(connection.RecoveryRequired);Assert.NotEqual(generation,mouse.ConnectionGeneration);
    }
    [Fact]
    public async Task CallerCancellationAndWaitingForTheWriterDoNotInvalidateAHealthyTransport()
    {
        var root=Temp();using var log=new AppLog(root);var stream=new StalledStream(Path.Combine(root,"hid.bin"));
        await using var connection=new HidConnection("healthy",[(Info(stream.Name),stream)],log,TimeSpan.FromMilliseconds(100));
        using(var cancel=new CancellationTokenSource(20))await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>connection.SendAsync(RootFeature.BuildGetFeature(0x1814).ToBytes(),cancel.Token));
        Assert.True(connection.IsOpen);Assert.False(connection.RecoveryRequired);
        var gate=(SemaphoreSlim)typeof(HidConnection).GetField("_writeGate",Hidden)!.GetValue(connection)!;await gate.WaitAsync();
        try{await Assert.ThrowsAsync<IOException>(()=>connection.SendAsync(RootFeature.BuildGetFeature(0x1814).ToBytes(),default));}
        finally{gate.Release();}
        Assert.True(connection.IsOpen);Assert.False(connection.RecoveryRequired);Assert.Equal(1,stream.Writes);
    }
    [Fact]
    public async Task ATimeoutDuringVerificationCannotBeReportedAsSuccessfulDeparture()
    {
        var root=Temp();using var log=new AppLog(root);var stream=new StalledStream(Path.Combine(root,"hid.bin")){SuccessfulWrites=1};
        await using var connection=new HidConnection("verify",[(Info(stream.Name),stream)],log,TimeSpan.FromMilliseconds(40));
        var mouse=Device(connection,log,"Mouse","마우스");
        var outcome=await new SwitchCoordinator(_=>{},verificationDelay:TimeSpan.Zero,retryDelay:TimeSpan.Zero).SwitchAsync(mouse,1,"fixture");
        Assert.Equal(SwitchOutcome.Cancelled,outcome);Assert.True(connection.RecoveryRequired);Assert.Equal(2,stream.Writes);
    }
    [Theory]
    [InlineData("ready")] [InlineData("wrong-device")] [InlineData("follow-off")] [InlineData("expired")] [InlineData("new-target")] [InlineData("keyboard-return")]
    public Task LatestFollowResumesOnlyAfterFreshLocalIdentityAndNeverNeedsReselection(string scenario)=>Sta(async()=>
    {
        var root=Temp();using var log=new AppLog(root);var failed=new Transport("mouse",fail:true);var keyboardWire=new Transport("keyboard",false);
        var mouse=Device(failed,log,"Mouse","마우스");var keyboard=Device(keyboardWire,log,"Keyboard","키보드");
        var store=new SettingsStore(Path.Combine(root,"settings"));store.Save(new(){FollowEnabled=true,SelectedDeviceIds=[keyboard.Id,mouse.Id],SelectedDeviceNames=[new(keyboard.Id,keyboard.Name,"키보드"),new(mouse.Id,mouse.Name,"마우스")]});
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var monitor=(DeviceMonitor)typeof(MainViewModel).GetField("_monitor",Hidden)!.GetValue(vm)!;
        var devices=(ConcurrentDictionary<(string,int),HostDevice>)typeof(DeviceMonitor).GetField("_devices",Hidden)!.GetValue(monitor)!;
        devices[(keyboardWire.Key,255)]=keyboard;devices[(failed.Key,255)]=mouse;
        ((ConcurrentDictionary<string,bool>)typeof(MainViewModel).GetField("_observedArrivals",Hidden)!.GetValue(vm)!)[mouse.Id]=true;
        typeof(MainViewModel).GetMethod("OnHostChanged",Hidden)!.Invoke(vm,[keyboard,1]);
        await Until(()=>typeof(MainViewModel).GetField("_pendingFollow",Hidden)!.GetValue(vm) is not null);
        Assert.Equal(1,failed.Writes);var selected=store.Value.SelectedDeviceIds!.ToArray();
        var freshWire=new Transport("mouse",false);var fresh=Device(freshWire,log,scenario=="wrong-device"?"Different mouse":"Mouse","마우스",local:false);
        devices[(freshWire.Key,255)]=fresh;
        typeof(MainViewModel).GetMethod("TryFollow",Hidden)!.Invoke(vm,null);Assert.Equal(0,freshWire.Writes);
        if(scenario=="follow-off")vm.FollowEnabled=false;
        if(scenario=="new-target")typeof(MainViewModel).GetMethod("OnHostChanged",Hidden)!.Invoke(vm,[keyboard,2]);
        if(scenario=="keyboard-return")
        {
            ((ConcurrentDictionary<string,bool>)typeof(MainViewModel).GetField("_observedArrivals",Hidden)!.GetValue(vm)!)[keyboard.Id]=true;
            ((Action<HostDevice>)typeof(DeviceMonitor).GetField("Arrived",Hidden)!.GetValue(monitor)!)(keyboard);
        }
        if(scenario=="expired")
        {
            var pending=typeof(MainViewModel).GetField("_pendingFollow",Hidden)!.GetValue(vm)!;
            pending.GetType().GetProperty("At")!.SetValue(pending,Environment.TickCount64-FollowPreparation.WaitMilliseconds-1);
        }
        fresh.ReportLocalHost(new(3,0));typeof(MainViewModel).GetMethod("TryFollow",Hidden)!.Invoke(vm,null);
        var shouldWrite=scenario is "ready" or "new-target";
        if(shouldWrite)await Until(()=>freshWire.Writes==1);else await Task.Delay(50);
        Assert.Equal(shouldWrite?1:0,freshWire.Writes);if(shouldWrite)Assert.Equal(scenario=="new-target"?2:1,freshWire.LastTarget);
        Assert.Equal(selected,store.Value.SelectedDeviceIds);Assert.False(vm.HardwareStarted);
    });
    private static HostDevice Device(IDeviceTransport transport,AppLog log,string name,string kind,bool local=true)
    {
        var device=new HostDevice(transport,255,log);
        typeof(HostDevice).GetField("_info",Hidden)!.SetValue(device,device.Info with{Name=name,Kind=kind,FeatureIndex=9});
        typeof(HostDevice).GetField("_metadataRead",Hidden)!.SetValue(device,true);
        if(local)device.ReportLocalHost(new(3,0));return device;
    }
    private sealed class Transport(string key,bool fail):IDeviceTransport
    {
        public string Key=>key;public string Name=>key;public string ConnectionLabel=>"Fixture";public bool IsReceiver=>false;public bool IsOpen{get;private set;}=true;public bool RecoveryRequired{get;private set;}public int SoftwareId=>9;public int Writes;public int LastTarget;
        public Task<byte[]?> RequestAsync(byte[] request,CancellationToken cancel)=>Task.FromResult<byte[]?>(null);
        public Task SendAsync(byte[] request,CancellationToken cancel){Writes++;LastTarget=request[4];IsOpen=false;if(fail){RecoveryRequired=true;throw new IOException("fixture write timeout");}return Task.CompletedTask;}
    }
    private sealed class StalledStream(string path):FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite)
    {
        public int Writes;public int SuccessfulWrites;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {if(++Writes<=SuccessfulWrites)return;await Task.Delay(Timeout.Infinite,cancellationToken);}
    }
    private static HidInterface Info(string path)=>new(path,0x046D,0xB042,0xFF43,0x0202,20,20,"Fixture");
    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static async Task Until(Func<bool> condition){var end=Environment.TickCount64+2500;while(!condition()){if(Environment.TickCount64>end)throw new TimeoutException("Recovery fixture");await Task.Delay(10);}}
    private static Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;dispatcher.BeginInvoke(async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
}
