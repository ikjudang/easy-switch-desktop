using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;
using EasySwitch.Core;
using EasySwitch.Hidpp;

namespace EasySwitch.Windows.Tests;

public sealed class ServiceTests
{
    [Fact]
    public void EngineSettingsDoNotCarryUiPreferencesOrExportPathsIntoSystemAccount()
    {
        var user=new AppSettings{FollowEnabled=true,SelectedDeviceIds=["k","m"],SelectedDeviceNames=[new("k","Key","키보드"),new("m","Mouse","마우스")],ExportDirectory="private-export",ExportEnabled=true,FloatingEnabled=true,FloatingSize=126};
        var preferences=EnginePreferences.From(user);preferences.Validate();var service=preferences.Apply(new());
        Assert.True(service.FollowEnabled);Assert.Equal(user.SelectedDeviceIds,service.SelectedDeviceIds);Assert.Null(service.ExportDirectory);Assert.False(service.FloatingEnabled);Assert.False(service.ExportEnabled);
        Assert.DoesNotContain("ExportDirectory",preferences.Fingerprint);Assert.Equal(preferences,JsonSerializer.Deserialize<EnginePreferences>(preferences.Fingerprint) is {} read?read with{SelectedDeviceIds=preferences.SelectedDeviceIds,SelectedDeviceNames=preferences.SelectedDeviceNames,ChannelLabels=preferences.ChannelLabels}:null);
    }
    [Fact]
    public void MalformedOrOversizedServiceSettingsAreRejected()
    {
        var good=EnginePreferences.From(new());
        Assert.Throws<InvalidDataException>(()=>(good with{SelectedDeviceNames=null!}).Validate());
        Assert.Throws<InvalidDataException>(()=>(good with{ChannelLabels=["one"]}).Validate());
        Assert.Throws<InvalidDataException>(()=>(good with{SelectedDeviceIds=Enumerable.Repeat("id",33).ToArray()}).Validate());
        Assert.Throws<InvalidDataException>(()=>(good with{MouseName=new string('x',513)}).Validate());
    }
    [Fact]
    public void JournalPagingDoesNotSkipRecordsAndRestartResetsCursor()
    {
        var journal=new EngineJournal();for(var i=0;i<750;i++)journal.Add(new LogEntry(DateTimeOffset.Now,"line"+i));
        var first=journal.Read(null,0);Assert.Equal(300,first.Sequence);Assert.Equal(300,first.Events.Length);
        var second=journal.Read(journal.Epoch,first.Sequence);Assert.Equal(301,second.Events[0].Sequence);Assert.Equal(600,second.Sequence);
        var last=journal.Read(journal.Epoch,second.Sequence);Assert.Equal(150,last.Events.Length);Assert.Equal(750,last.Sequence);
        var restart=new EngineJournal();restart.Add(new LogEntry(DateTimeOffset.Now,"new"));Assert.Single(restart.Read(journal.Epoch,750).Events);
    }
    [Fact]
    public void AGapIsExplicitAndOriginalTimesSurviveRemoteImport()
    {
        var journal=new EngineJournal();var at=new DateTimeOffset(2026,10,23,12,0,0,123,TimeSpan.FromHours(9));
        for(var i=0;i<1500;i++)journal.Add(new LogEntry(at,"line"+i));Assert.True(journal.Read(journal.Epoch,1).Truncated);
        var root=Temp();using var log=new AppLog(root);log.Import(new LogEntry(at,"remote detail"));log.Import(new CodeLogEntry(at,"ES1|R0001|END|E123"));
        Assert.Equal(at,log.Recent().Single().At);Assert.Equal(at,log.RecentCodes().Single().At);Assert.Contains("12:00:00.123",ReadShared(log.FilePath));Assert.Contains("20261023T120000.123+09:00",ReadShared(log.CodeFilePath));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(ServiceWire.MaximumBytes+1)]
    public async Task InvalidLengthIsRejectedBeforePayloadAllocation(int length)
    {
        await using var stream=new MemoryStream(BitConverter.GetBytes(length));await Assert.ThrowsAsync<InvalidDataException>(()=>ServiceWire.ReadAsync<ServiceRequest>(stream,default));
    }
    [Fact]
    public async Task WireRoundTripPreservesRequestAndNoticeFields()
    {
        await using var stream=new MemoryStream();var request=new ServiceRequest("Switch",Target:2,Reason:"플로팅 채널 버튼",ExpiresAt:123456);
        await ServiceWire.WriteAsync(stream,request,default);stream.Position=0;Assert.Equal(request,await ServiceWire.ReadAsync<ServiceRequest>(stream,default));
    }
    [Fact]
    public async Task ServicePersistsConfigurationAcrossUiDisconnectAndRecoversAfterPowerEvents()
    {
        var root=Temp();var name="EasySwitch.Test."+Guid.NewGuid().ToString("N");
        await using var runtime=new BackgroundRuntime(root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        using var uiLog=new AppLog(Path.Combine(root,"ui"));
        var states=new ConcurrentQueue<ServiceSnapshot>();
        var desired=EnginePreferences.From(new AppSettings{FollowEnabled=true,SelectedDeviceIds=["k","m"],SelectedDeviceNames=[new("k","Keyboard","키보드"),new("m","Mouse","마우스")]});
        await using(var client=new ServiceClient(uiLog,name,Environment.ProcessId))
        {
            client.SnapshotReceived+=states.Enqueue;client.Start();await Until(()=>client.Connected);
            client.Configure(desired);await Until(()=>states.Any(s=>s.Preferences.FollowEnabled));await client.FlushConfigurationAsync();
        }
        // Closing the GUI connection does not stop the engine or erase boot settings.
        using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(3000);await ServiceWire.WriteAsync(pipe,new ServiceRequest(),default);var reply=await ServiceWire.ReadAsync<ServiceReply>(pipe,default);
            Assert.True(reply.State!.Preferences.FollowEnabled);Assert.Equal(new[]{"k","m"},reply.State.Preferences.SelectedDeviceIds);
            await ServiceWire.WriteAsync(pipe,new ServiceRequest("Switch",Target:2,ExpiresAt:Environment.TickCount64-1),default);
            Assert.Contains("오래된",(await ServiceWire.ReadAsync<ServiceReply>(pipe,default)).Error);
        }
        Assert.True(new SettingsStore(Path.Combine(root,"engine")).Value.FollowEnabled);
        runtime.Power(true,"fixture suspend");runtime.Power(false,"fixture resume");runtime.Power(false,"duplicate resume");
        await using var reconnected=new ServiceClient(uiLog,name,Environment.ProcessId);reconnected.SnapshotReceived+=states.Enqueue;reconnected.Start();
        await Until(()=>states.SelectMany(s=>s.Events).Any(e=>e.Code?.Code.Contains("POWER|RESUME")==true));
        Assert.DoesNotContain(states.SelectMany(s=>s.Events),e=>e.Code?.Code.Contains("BEGIN")==true);
        using(var stale=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            await stale.ConnectAsync(3000);await ServiceWire.WriteAsync(stale,new ServiceRequest("Switch",Target:1,ExpiresAt:Environment.TickCount64+2000,PowerGeneration:0),default);
            Assert.Contains("전원 상태",(await ServiceWire.ReadAsync<ServiceReply>(stale,default)).Error);
        }
    }
    [Fact]
    public async Task FrontendNeverStartsItsOwnHidMonitorAndReceivesSharedSelection()
    {
        var root=Temp();var name="EasySwitch.Test."+Guid.NewGuid().ToString("N");
        await using var runtime=new BackgroundRuntime(root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        await Sta(async()=>
        {
            var store=new SettingsStore(Path.Combine(root,"frontend"));using var log=new AppLog(Path.Combine(root,"frontend","logs"));
            await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);var client=new ServiceClient(log,name,Environment.ProcessId);vm.AttachService(client);vm.Start();
            Assert.False(vm.HardwareStarted);await Until(()=>client.Connected);
            vm.FollowEnabled=true;await Until(()=>new SettingsStore(Path.Combine(root,"engine")).Value.FollowEnabled);
            Assert.False(vm.HardwareStarted);await vm.SwitchAsync(1,"트레이 채널 버튼");await Until(()=>vm.Logs.Any(e=>e.Message.Contains("선택한 기기에 사용할 수 있는 통로가 없음")));
            Assert.False(vm.HardwareStarted);
        });
    }
    [Fact]
    public async Task RestartedServiceKeepsTheSelectedDevicesAndFollowSettingWithoutUiReselection()
    {
        var root=Temp();var name="EasySwitch.Test."+Guid.NewGuid().ToString("N");var owner=WindowsIdentity.GetCurrent().User!.Value;
        var expected=EnginePreferences.From(new AppSettings{FollowEnabled=true,SelectedDeviceIds=["saved-k","saved-m"],SelectedDeviceNames=[new("saved-k","Keyboard","키보드"),new("saved-m","Mouse","마우스")]});
        await using(var first=new BackgroundRuntime(root,name,owner,true))
        {
            await first.StartAsync();using var log=new AppLog(Path.Combine(root,"first-ui"));await using var client=new ServiceClient(log,name,Environment.ProcessId);
            client.Configure(expected);client.Start();await Until(()=>client.Connected);await client.FlushConfigurationAsync();
        }
        await using var restarted=new BackgroundRuntime(root,name,owner,true);await restarted.StartAsync();
        using var secondLog=new AppLog(Path.Combine(root,"second-ui"));await using var readOnly=new ServiceClient(secondLog,name,Environment.ProcessId);
        var states=new ConcurrentQueue<ServiceSnapshot>();readOnly.SnapshotReceived+=states.Enqueue;readOnly.Start();await Until(()=>states.Count>0);
        Assert.Equal(expected.Fingerprint,states.Last().Preferences.Fingerprint);
        Assert.DoesNotContain(states.SelectMany(s=>s.Events),e=>e.Code?.Code.Contains("BEGIN")==true);
    }
    [Fact]
    public async Task FullLogExportIncludesRecordsOlderThanTheUiHistory()
    {
        var root=Temp();var name="EasySwitch.Test."+Guid.NewGuid().ToString("N");
        await using var runtime=new BackgroundRuntime(root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        var engineLog=(AppLog)typeof(BackgroundRuntime).GetField("_log",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime)!;
        for(var i=0;i<1600;i++){engineLog.Write("fixture record "+i);engineLog.WriteCode($"ES1 R{i:D4} END E{i}");}
        using var local=new AppLog(Path.Combine(root,"ui"));await using var client=new ServiceClient(local,name,Environment.ProcessId);client.Start();await Until(()=>client.Connected);
        var details=Path.Combine(root,"export.txt");var codes=Path.Combine(root,"export.codes.txt");await client.ExportAsync(details,false);await client.ExportAsync(codes,true);
        Assert.Contains("fixture record 0",File.ReadAllText(details));Assert.Contains("fixture record 1599",File.ReadAllText(details));Assert.Contains("R0000",File.ReadAllText(codes));Assert.Contains("R1599",File.ReadAllText(codes));
    }
    [Fact]
    public async Task WrongPipeServerIdentityCannotReceiveSwitchCommands()
    {
        var root=Temp();var name="EasySwitch.Test."+Guid.NewGuid().ToString("N");
        await using var runtime=new BackgroundRuntime(root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(root,"ui"));await using var client=new ServiceClient(log,name,Environment.ProcessId+1);client.Start();
        await Until(()=>log.Recent().Any(e=>e.Message.Contains("owner mismatch")));Assert.False(client.Connected);
        await Assert.ThrowsAsync<IOException>(()=>client.SwitchAsync(0,"test"));
    }
    [Fact]
    public async Task MultipleFrontendSessionsCanReadTheSingleEngineAtTheSameTime()
    {
        var root=Temp();var name="EasySwitch.Test."+Guid.NewGuid().ToString("N");
        await using var runtime=new BackgroundRuntime(root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(root,"ui"));await using var a=new ServiceClient(log,name,Environment.ProcessId);await using var b=new ServiceClient(log,name,Environment.ProcessId);
        var first=new ConcurrentQueue<ServiceSnapshot>();var second=new ConcurrentQueue<ServiceSnapshot>();a.SnapshotReceived+=first.Enqueue;b.SnapshotReceived+=second.Enqueue;a.Start();b.Start();
        await Until(()=>first.Count>0 && second.Count>0);Assert.Equal(first.First().Epoch,second.First().Epoch);
    }
    [Fact]
    public async Task SuspendCancelsPendingWritesAndResumeDoesNotReplayThem()
    {
        await Sta(async()=>
        {
            var root=Temp();using var log=new AppLog(root);await using var vm=new MainViewModel(new SettingsStore(root),log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
            var coordinator=(SwitchCoordinator)typeof(MainViewModel).GetField("_switches",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
            var device=new CancelDevice();var pending=coordinator.SwitchAsync(device,1,"fixture");await Until(()=>device.Writes==1);
            await vm.ChangePowerAsync(true,"fixture suspend");Assert.Equal(SwitchOutcome.Cancelled,await pending);Assert.Equal(1,device.Writes);
            await vm.ChangePowerAsync(false,"fixture resume");await Task.Delay(30);Assert.Equal(1,device.Writes);Assert.False(vm.HardwareStarted);
            device.Block=false;Assert.Equal(SwitchOutcome.TargetReported,await coordinator.SwitchAsync(device,2,"new request"));Assert.Equal(2,device.Writes);
        });
    }
    private sealed class CancelDevice:IHostDevice
    {
        public string Id=>"fixture";public string Name=>"Mouse";public bool TransportAvailable=>true;public int HostCount=>3;public int? CurrentHost=>0;
        public int Writes;public bool Block=true;private int _target;
        public async Task SetHostAsync(int target,CancellationToken token){Writes++;_target=target;if(Block)await Task.Delay(Timeout.Infinite,token);}
        public Task<HostInfo?> ReadHostAsync(CancellationToken token)=>Task.FromResult<HostInfo?>(new(3,_target));
    }
    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string ReadShared(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    private static async Task Until(Func<bool> condition)
    {var until=Environment.TickCount64+10000;while(!condition()){if(Environment.TickCount64>until)throw new TimeoutException("Service fixture timed out");await Task.Delay(20);}}
    private static Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;dispatcher.BeginInvoke(async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
}
