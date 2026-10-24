using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using EasySwitch.Core;
using EasySwitch.Windows.Floating;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

public sealed class KeyboardFeedbackTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    [Fact]
    public async Task AlwaysShowDefaultsOffAndPersistsWithoutEnablingTheFeature()
    {
        var root=Temp();var store=new SettingsStore(root);using var log=new AppLog(Path.Combine(root,"logs"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
        Assert.False(vm.FloatingAlwaysShow);var changed=0;vm.FloatingSettingsChanged+=()=>changed++;
        vm.FloatingAlwaysShow=true;vm.FloatingAlwaysShow=true;
        Assert.Equal(1,changed);Assert.True(new SettingsStore(root).Value.FloatingAlwaysShow);Assert.True(FloatingPreferences.From(store.Value).AlwaysShow);Assert.False(vm.FloatingEnabled);
        Assert.False(EnginePreferences.From(store.Value).Apply(new()).FloatingAlwaysShow);
    }
    [Fact]
    public void DuplicateFilterMatchesTheAndroidWindowAndDoesNotSwallowAFreshArrival()
    {
        var root=Temp();using var log=new AppLog(root);var device=Keyboard(new Transport(),log);long at=100;
        var filter=new KeyboardDepartureFeedback(()=>at);
        Assert.False(filter.TryAccept(device,0));Assert.False(filter.TryAccept(device,-1));Assert.False(filter.TryAccept(device,3));
        Assert.True(filter.TryAccept(device,1));at=349;Assert.False(filter.TryAccept(device,1));at=350;Assert.True(filter.TryAccept(device,1));
        Assert.True(filter.TryAccept(device,2));Assert.False(filter.TryAccept(device,2));filter.Arrived(device);Assert.True(filter.TryAccept(device,2));
        typeof(HostDevice).GetField("_generation",Hidden)!.SetValue(device,2L);Assert.True(filter.TryAccept(device,2));
    }
    [Fact]
    public async Task AcceptedDepartureNotifiesWhileFollowIsOffAndIgnoresManualEchoWithoutWritingHardware()
    {
        var root=Temp();var store=new SettingsStore(root);using var log=new AppLog(Path.Combine(root,"logs"));var transport=new Transport();var device=Keyboard(transport,log);
        store.Save(new(){SelectedDeviceIds=[device.Id],SelectedDeviceNames=[new(device.Id,device.Name,"키보드")]});
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([device.Info]);
        var targets=new List<int>();vm.KeyboardSwitchObserved+=targets.Add;
        var manual=(ManualSwitchGuard)typeof(MainViewModel).GetField("_manual",Hidden)!.GetValue(vm)!;
        var hostChanged=typeof(MainViewModel).GetMethod("OnHostChanged",Hidden)!;
        manual.Begin(0,1);hostChanged.Invoke(vm,[device,1]);Assert.Empty(targets);manual.Complete(0);
        hostChanged.Invoke(vm,[device,1]);hostChanged.Invoke(vm,[device,1]);Assert.Equal(new[]{1},targets);
        hostChanged.Invoke(vm,[device,0]);Assert.Equal(new[]{1},targets);Assert.Equal(0,transport.Writes);Assert.False(vm.FollowEnabled);
    }
    [Fact]
    public async Task ServiceFeedbackIsOneEventAndNeverReplayedForToastUpdatesOrSnapshotRetries()
    {
        var root=Temp();using var log=new AppLog(root);var store=new SettingsStore(root);await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
        var targets=new List<int>();vm.KeyboardSwitchObserved+=targets.Add;
        var receive=typeof(MainViewModel).GetMethod("ReceiveServiceSnapshot",Hidden)!;var prefs=EnginePreferences.From(store.Value);var at=DateTimeOffset.Now;
        ServiceSnapshot Snapshot(string epoch,params EngineEvent[] events)=>new(epoch,events.Last().Sequence,[],prefs,null,"fixture",events);
        var signal=Snapshot("first",new EngineEvent(10,at,KeyboardTarget:2));receive.Invoke(vm,[signal,true]);receive.Invoke(vm,[signal,true]);Assert.Equal(new[]{2},targets);
        receive.Invoke(vm,[Snapshot("first",new EngineEvent(11,at,Notice:new("channel",[new("Key",true)],1))),true]);Assert.Equal(new[]{2},targets);
        receive.Invoke(vm,[Snapshot("first",new EngineEvent(12,at.AddSeconds(-3),KeyboardTarget:1)),true]);
        receive.Invoke(vm,[Snapshot("first",new EngineEvent(13,at,KeyboardTarget:3)),true]);Assert.Equal(new[]{2},targets);
        receive.Invoke(vm,[Snapshot("second",new EngineEvent(1,DateTimeOffset.Now,KeyboardTarget:0)),true]);Assert.Equal(new[]{2,0},targets);
        Assert.False(vm.HardwareStarted);
    }
    [Fact]
    public async Task JournalAndWireKeepKeyboardSignalsSeparateFromResultNotices()
    {
        var journal=new EngineJournal();journal.AddKeyboardSwitch(1);var events=journal.Read(null,0).Events;var signal=Assert.Single(events);
        Assert.Equal(1,signal.KeyboardTarget);Assert.Null(signal.Notice);Assert.Null(signal.Log);Assert.Null(signal.Code);
        await using var stream=new MemoryStream();await ServiceWire.WriteAsync(stream,signal,default);stream.Position=0;
        Assert.Equal(signal,await ServiceWire.ReadAsync<EngineEvent>(stream,default));
        Assert.Null(JsonSerializer.Deserialize<EngineEvent>("{\"Sequence\":1,\"At\":\"2026-10-23T12:00:00+09:00\"}")!.KeyboardTarget);
    }
    private static HostDevice Keyboard(IDeviceTransport transport,AppLog log)
    {
        var device=new HostDevice(transport,0xFF,log);var info=device.Info with{Name="Keyboard",Kind="키보드",Online=true,HostCount=3,CurrentHost=0};
        typeof(HostDevice).GetField("_info",Hidden)!.SetValue(device,info);return device;
    }
    private sealed class Transport:IDeviceTransport
    {
        public string Key=>"keyboard-feedback-fixture";public string Name=>"Keyboard";public string ConnectionLabel=>"Fixture";public bool IsReceiver=>false;public bool IsOpen=>true;public int SoftwareId=>9;public int Writes;
        public Task<byte[]?> RequestAsync(byte[] request,CancellationToken cancel){Interlocked.Increment(ref Writes);return Task.FromResult<byte[]?>(null);}
        public Task SendAsync(byte[] request,CancellationToken cancel){Interlocked.Increment(ref Writes);return Task.CompletedTask;}
    }
    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
}
