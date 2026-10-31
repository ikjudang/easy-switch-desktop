using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

public sealed class SelectionSynchronizationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
    private static AppSettings Pair(long revision=0)=>new(){FollowEnabled=true,SelectedDeviceIds=["k","m"],SelectedDeviceNames=[new("k","Keyboard","키보드"),new("m","Mouse","마우스")],EngineSettingsRevision=revision};
    private static AppSettings Mouse(long revision=0)=>Pair(revision) with{SelectedDeviceIds=["m"],SelectedDeviceNames=[new("m","Mouse","마우스")]};
    private static string Pipe()=>"EasySwitch.Selection."+Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("legacy-ui")] [InlineData("empty-ui")] [InlineData("missing-ui")]
    [InlineData("malformed-ui")] [InlineData("no-selection-ui")] [InlineData("no-selection-newer-ui")]
    [InlineData("newer-ui")] [InlineData("newer-service")] [InlineData("newer-empty-service")]
    [InlineData("legacy-ids")]
    public Task StartupRestoresTheCorrectSavedSelection(string scenario)=>Sta(async()=>
    {
        var uiDirectory=Path.Combine(_root,"ui");var engineDirectory=Path.Combine(_root,"engine");
        var local=Pair();var remote=Mouse();var expected=new[]{"k","m"};
        switch(scenario)
        {
            case "empty-ui":local=Pair() with{SelectedDeviceIds=[],SelectedDeviceNames=[]};remote=Pair();expected=[];break;
            case "missing-ui":case "malformed-ui":case "no-selection-ui":remote=Pair();break;
            case "no-selection-newer-ui":local=new(){Language="en",ToastSize=75,EngineSettingsRevision=20};remote=Pair(10);break;
            case "newer-ui":local=Pair(20);remote=Mouse(10);break;
            case "newer-service":local=Pair(10);remote=Mouse(20);expected=["m"];break;
            case "newer-empty-service":local=Pair(10);remote=Pair(20) with{SelectedDeviceIds=[],SelectedDeviceNames=[]};expected=[];break;
            case "legacy-ids":local=new(){KeyboardId="k",KeyboardName="Keyboard",MouseId="m",MouseName="Mouse",FollowEnabled=true};break;
        }
        if(scenario=="malformed-ui"){Directory.CreateDirectory(uiDirectory);File.WriteAllText(Path.Combine(uiDirectory,"settings.json"),"unreadable selection fixture");}
        else if(scenario!="missing-ui")new SettingsStore(uiDirectory).Save(scenario=="no-selection-ui"?new(){ToastSize=75}:local);
        new SettingsStore(engineDirectory).Save(remote);
        var name=Pipe();await using var runtime=new BackgroundRuntime(_root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        var store=new SettingsStore(uiDirectory);using var log=new AppLog(Path.Combine(_root,"ui-log"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var client=new ServiceClient(log,name,Environment.ProcessId);vm.AttachService(client);vm.Start();
        await Until(()=>client.Connected && !client.ConfigurationPending && Same(store.Value,expected) && Same(new SettingsStore(engineDirectory).Value,expected));
        Assert.Equal(expected,Ids(new SettingsStore(uiDirectory).Value));Assert.False(vm.HardwareStarted);
        if(expected.Contains("k")){Assert.True(vm.IsSelectedDevice("k"));Assert.False(vm.DeviceChoices.Single(d=>d.Info.Id=="k").Info.Online);}
        if(scenario is "no-selection-ui" or "no-selection-newer-ui")Assert.Equal(75,store.Value.ToastSize);
        Assert.True(Directory.GetFiles(Path.Combine(uiDirectory,"selection-backups"),"*.json").Length>0);
        if(scenario=="malformed-ui")Assert.Contains("unreadable selection fixture",File.ReadAllText(Directory.GetFiles(Path.Combine(uiDirectory,"selection-backups"),"*.json").Single()));
    });

    [Fact]
    public Task AQueuedOldStateCannotUndoAConfirmedUserDeselection()=>Sta(async()=>
    {
        new SettingsStore(Path.Combine(_root,"engine")).Save(Pair());var store=new SettingsStore(Path.Combine(_root,"ui"));store.Save(Pair());
        var name=Pipe();await using var runtime=new BackgroundRuntime(_root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(_root,"ui-log"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var client=new ServiceClient(log,name,Environment.ProcessId);var states=new ConcurrentQueue<ServiceSnapshot>();client.SnapshotReceived+=states.Enqueue;
        vm.AttachService(client);vm.Start();await Until(()=>states.Count>0 && !client.ConfigurationPending);
        var old=states.Last();vm.ToggleDevice(vm.DeviceChoices.Single(d=>d.Info.Id=="k").Info);
        await Until(()=>!client.ConfigurationPending && Same(new SettingsStore(Path.Combine(_root,"engine")).Value,["m"]));
        Receive(vm,old);await Task.Delay(100);
        Assert.Equal(new[]{"m"},Ids(store.Value));Assert.Equal(new[]{"m"},Ids(new SettingsStore(Path.Combine(_root,"ui")).Value));
        Assert.False(client.CanApplyPreferences(old));Assert.False(vm.HardwareStarted);
    });

    [Fact]
    public Task APreviousServiceConnectionCannotOverwriteAReconnectedSelection()=>Sta(async()=>
    {
        var engineDirectory=Path.Combine(_root,"engine");new SettingsStore(engineDirectory).Save(Pair(10));
        var store=new SettingsStore(Path.Combine(_root,"ui"));store.Save(Pair(10));var name=Pipe();
        var first=new BackgroundRuntime(_root,name,WindowsIdentity.GetCurrent().User!.Value,true);await first.StartAsync();
        using var log=new AppLog(Path.Combine(_root,"ui-log"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var client=new ServiceClient(log,name,Environment.ProcessId);var states=new ConcurrentQueue<ServiceSnapshot>();client.SnapshotReceived+=states.Enqueue;
        vm.AttachService(client);vm.Start();
        try{await Until(()=>states.Count>0 && !client.ConfigurationPending);}finally{await first.DisposeAsync();}
        var old=states.Last();await Until(()=>!client.Connected);new SettingsStore(engineDirectory).Save(Mouse(20));
        await using var second=new BackgroundRuntime(_root,name,WindowsIdentity.GetCurrent().User!.Value,true);await second.StartAsync();
        await Until(()=>client.Connected && client.ConnectionGeneration>old.ClientConnection && Same(store.Value,["m"]));
        Receive(vm,old);await Task.Delay(100);
        Assert.Equal(new[]{"m"},Ids(store.Value));Assert.False(client.IsCurrent(old));
    });

    [Fact]
    public Task AFailedServiceSaveNeverAcknowledgesOrErasesTheRestoredKeyboard()=>Sta(async()=>
    {
        var engineDirectory=Path.Combine(_root,"engine");new SettingsStore(engineDirectory).Save(Mouse());
        var store=new SettingsStore(Path.Combine(_root,"ui"));store.Save(Pair());var name=Pipe();
        await using var runtime=new BackgroundRuntime(_root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(_root,"ui-log"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var client=new ServiceClient(log,name,Environment.ProcessId);vm.AttachService(client);
        using(var held=new FileStream(Path.Combine(engineDirectory,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            vm.Start();await Until(()=>log.Recent().Any(e=>e.Message.Contains("서비스 설정 저장을 확인하지 못했어")));
            Assert.True(client.ConfigurationPending);Assert.Equal(new[]{"k","m"},Ids(store.Value));
            Assert.Equal(new[]{"k","m"},Ids(new SettingsStore(Path.Combine(_root,"ui")).Value));
        }
        await Until(()=>client.Connected && !client.ConfigurationPending && Same(new SettingsStore(engineDirectory).Value,["k","m"]));
        Assert.False(vm.HardwareStarted);
    });

    [Fact]
    public Task AUserChangeDuringInitialComparisonWinsOverStartupRestoration()=>Sta(async()=>
    {
        new SettingsStore(Path.Combine(_root,"engine")).Save(Mouse());var store=new SettingsStore(Path.Combine(_root,"ui"));store.Save(Pair());
        var name=Pipe();await using var runtime=new BackgroundRuntime(_root,name,WindowsIdentity.GetCurrent().User!.Value,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(_root,"ui-log"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var client=new ServiceClient(log,name,Environment.ProcessId);vm.AttachService(client);
        client.InitialPreferencesCompared+=_=>client.Configure(EnginePreferences.From(Mouse(30)));vm.Start();
        await Until(()=>client.Connected && !client.ConfigurationPending && Same(store.Value,["m"]));
        Assert.Equal(1,client.ConfigurationRevision);Assert.Equal(new[]{"m"},Ids(new SettingsStore(Path.Combine(_root,"engine")).Value));
    });

    [Fact]
    public Task LegacyServiceRepliesKeepTheLocalRevisionWithoutRepeatedSaves()=>Sta(async()=>
    {
        var store=new SettingsStore(Path.Combine(_root,"ui"));store.Save(Pair(42));
        using var log=new AppLog(Path.Combine(_root,"ui-log"));var name=Pipe();using var stop=new CancellationTokenSource();
        var server=Task.Run(async()=>
        {
            using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(stop.Token);var settings=EnginePreferences.From(Mouse());
            while(!stop.IsCancellationRequested)
            {
                var request=await ServiceWire.ReadAsync<ServiceRequest>(pipe,stop.Token);
                if(request.Preferences is {} incoming)settings=incoming with{Revision=0};
                var reply=new ServiceReply(new("legacy",0,[],settings,null,"",[]));
                var json=JsonSerializer.SerializeToNode(reply)!;json["State"]!["Preferences"]!.AsObject().Remove("Revision");
                await ServiceWire.WriteAsync(pipe,json,stop.Token);
            }
        });
        try
        {
            await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);var client=new ServiceClient(log,name,Environment.ProcessId);var snapshots=0;var saves=0;
            store.Saved+=_=>saves++;client.SnapshotReceived+=_=>Interlocked.Increment(ref snapshots);vm.AttachService(client);vm.Start();
            await Until(()=>!client.ConfigurationPending && Volatile.Read(ref snapshots)>4);
            Assert.Equal(new[]{"k","m"},Ids(store.Value));Assert.Equal(42,store.Value.EngineSettingsRevision);Assert.Equal(0,saves);
        }
        finally
        {
            await stop.CancelAsync();try{await server;}catch(OperationCanceledException){}catch(IOException)when(stop.IsCancellationRequested){}
        }
    });

    [Fact]
    public Task OnlyEngineChangesAdvanceThePersistedRevision()=>Sta(async()=>
    {
        var store=new SettingsStore(_root);store.Save(Pair(10));using var log=new AppLog(Path.Combine(_root,"log"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        vm.ToastSize=75;vm.ToastTransparency=40;vm.FloatingEnabled=true;Assert.Equal(10,store.Value.EngineSettingsRevision);
        vm.FollowEnabled=false;var changed=store.Value.EngineSettingsRevision;Assert.True(changed>10);
        vm.ToastEnabled=false;Assert.Equal(changed,store.Value.EngineSettingsRevision);
        vm.ApplyEnginePreferences(EnginePreferences.From(store.Value) with{Revision=changed+10});
        Assert.Equal(changed+10,store.Value.EngineSettingsRevision);Assert.Equal(changed+10,new SettingsStore(_root).Value.EngineSettingsRevision);
    });

    [Fact]
    public async Task LocalReceiveStampsNeverEnterTheWireAndOldMessagesDefaultToLegacyRevision()
    {
        var legacy=JsonSerializer.Deserialize<EnginePreferences>("{\"FollowEnabled\":true,\"SelectedDeviceIds\":[\"k\"],\"SelectedDeviceNames\":[],\"Language\":\"ko\",\"ChannelLabels\":[\"\",\"\",\"\"]}")!;
        Assert.Equal(0,legacy.Revision);legacy.Validate();
        var state=new ServiceSnapshot("epoch",1,[],legacy,null,"",[]) with{ClientConnection=99,ClientRevision=77};
        await using var stream=new MemoryStream();await ServiceWire.WriteAsync(stream,new ServiceReply(state),default);
        Assert.DoesNotContain("ClientRevision",System.Text.Encoding.UTF8.GetString(stream.ToArray()));stream.Position=0;
        var copy=(await ServiceWire.ReadAsync<ServiceReply>(stream,default)).State!;Assert.Equal(0,copy.ClientRevision);Assert.Equal(0,copy.ClientConnection);
        Assert.Throws<InvalidDataException>(()=>(legacy with{Revision=-1}).Validate());
    }

    private static void Receive(MainViewModel vm,ServiceSnapshot state)=>typeof(MainViewModel).GetMethod("ReceiveServiceSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[state,true]);
    private static string[] Ids(AppSettings settings)=>settings.SelectedDeviceIds??new[]{settings.KeyboardId,settings.MouseId}.Where(id=>!string.IsNullOrEmpty(id)).Cast<string>().ToArray();
    private static bool Same(AppSettings settings,string[] expected)=>Ids(settings).SequenceEqual(expected);
    private static async Task Until(Func<bool> condition){var until=Environment.TickCount64+7000;while(!condition()){if(Environment.TickCount64>until)throw new TimeoutException("Selection synchronization fixture timed out");await Task.Delay(20);}}
    private static Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{var dispatcher=Dispatcher.CurrentDispatcher;dispatcher.BeginInvoke(async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
