using System.IO;
using System.ServiceProcess;
using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

// The user declined the service approval and the registered service is not running: nothing follows
// the keyboard, so the window must say so instead of showing "follow on".
[Collection(nameof(ApplicationResourcesCollection))]
public sealed class FollowStoppedNoticeTests : IDisposable
{
    private const string Owner="S-1-5-21-1-2-3-1001";
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));

    private static BootServiceDecision Decide(string? declined,bool registered,ServiceControllerStatus? status)
        =>BootServicePolicy.Decide(false,false,declined,registered?new ServiceInstallation("0.2.42",Owner,"x"):null,new(registered,status,false),Owner,new Version(0,2,42),"0.2.42",false,true);

    [Fact]
    public void DecliningWhileTheRegisteredServiceIsNotRunningIsFlagged()
    {
        var stopped=Decide("0.2.42",true,ServiceControllerStatus.Stopped);
        Assert.Equal(BootServiceAction.Skip,stopped.Action);Assert.True(stopped.DeclinedWhileStopped);
        // A running service still follows the keyboard, and an unregistered PC follows in the app itself.
        Assert.False(Decide("0.2.42",true,ServiceControllerStatus.Running).DeclinedWhileStopped);
        Assert.False(Decide("0.2.42",false,null).DeclinedWhileStopped);
        // Without a decline the app asks again, so nothing is flagged.
        var prompt=Decide(null,true,ServiceControllerStatus.Stopped);
        Assert.Equal(BootServiceAction.Prompt,prompt.Action);Assert.False(prompt.DeclinedWhileStopped);
    }

    [Fact]
    public Task TheWindowSaysFollowingIsOffAndHowToFixItUntilTheServiceConnects()=>Sta(async()=>
    {
        var store=new SettingsStore(_directory);using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        await using var client=new ServiceClient(log,"EasySwitch.Tests."+Guid.NewGuid().ToString("N"),Environment.ProcessId);
        vm.AttachService(client);
        var raised=new List<string?>();vm.PropertyChanged+=(_,args)=>raised.Add(args.PropertyName);
        ServiceRegistration.FollowStoppedAfterDecline=false;
        try
        {
            Assert.NotEqual(vm.T("서비스 멈춤 · 따라가기 꺼짐","Service stopped · Follow off"),vm.FollowStatus);
            Assert.Null(vm.FollowStatusHint);
            Assert.Equal(vm.FollowEnabled,vm.FollowIndicatorOn);

            ServiceRegistration.FollowStoppedAfterDecline=true;
            Assert.Equal(vm.T("서비스 멈춤 · 따라가기 꺼짐","Service stopped · Follow off"),vm.FollowStatus);
            var hint=vm.FollowStatusHint;
            Assert.NotNull(hint);
            Assert.True(hint!.Contains("로그인 전 실행 설치·업데이트") || hint.Contains("Install or update boot service"),hint);
            Assert.False(vm.FollowIndicatorOn);

            // Once the app is connected to a running service the notice disappears and the window is told.
            // The event has no public raiser, so call its backing delegate the way the client does.
            var raise=(Action<bool>?)typeof(ServiceClient).GetField("AvailabilityChanged",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(client);
            Assert.NotNull(raise);
            raise!(true);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.NotEqual(vm.T("서비스 멈춤 · 따라가기 꺼짐","Service stopped · Follow off"),vm.FollowStatus);
            Assert.Null(vm.FollowStatusHint);
            Assert.Contains(nameof(MainViewModel.FollowStatus),raised);
            Assert.Contains(nameof(MainViewModel.FollowIndicatorOn),raised);
        }
        finally{ServiceRegistration.FollowStoppedAfterDecline=false;}
    });

    private static Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
