using System.IO;
using System.Windows.Threading;
using EasySwitch.Windows.Notices;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

public sealed class ToastSettingsTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingSettingsKeepTheOriginalToastAppearance()
    {
        Directory.CreateDirectory(_directory);File.WriteAllText(Path.Combine(_directory,"settings.json"),"{\"FloatingSize\":126}");
        var store=new SettingsStore(_directory);
        Assert.True(store.Value.ToastEnabled);Assert.Equal(100,store.Value.ToastSize);Assert.Equal(0,store.Value.ToastTransparency);
        Assert.Equal(126,store.Value.FloatingSize);
    }

    [Theory]
    [InlineData(-1,50,-1,0)] [InlineData(50,50,0,0)] [InlineData(75,75,40,40)] [InlineData(200,200,100,100)] [InlineData(999,200,999,100)]
    public void ToastRangesAreClampedAndPersist(int size,int expectedSize,int transparency,int expectedTransparency)
    {
        var store=new SettingsStore(_directory);store.Save(new(){ToastEnabled=false,ToastSize=size,ToastTransparency=transparency});
        var reopened=new SettingsStore(_directory).Value;
        Assert.False(reopened.ToastEnabled);Assert.Equal(expectedSize,reopened.ToastSize);Assert.Equal(expectedTransparency,reopened.ToastTransparency);
    }

    [Fact]
    public async Task ToastControlsPersistIndependentlyOfFloatingAndEngineSettings()
    {
        var store=new SettingsStore(_directory);store.Save(new(){FollowEnabled=true,FloatingEnabled=true,FloatingSize=126,FloatingTransparency=30});
        using var log=new AppLog(Path.Combine(_directory,"logs"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var toastChanges=0;var floatingChanges=0;vm.ToastSettingsChanged+=()=>toastChanges++;vm.FloatingSettingsChanged+=()=>floatingChanges++;
        vm.ToastEnabled=false;vm.ToastSize=70;vm.ToastTransparency=60;vm.ToastTransparency=60;
        Assert.Equal(3,toastChanges);Assert.Equal(0,floatingChanges);
        Assert.True(vm.FollowEnabled);Assert.True(vm.FloatingEnabled);Assert.Equal(126,vm.FloatingSize);Assert.Equal(30,vm.FloatingTransparency);
        var reopened=new SettingsStore(_directory).Value;
        Assert.False(reopened.ToastEnabled);Assert.Equal(70,reopened.ToastSize);Assert.Equal(60,reopened.ToastTransparency);
        var applied=EnginePreferences.From(new AppSettings()).Apply(reopened);
        Assert.False(applied.ToastEnabled);Assert.Equal(70,applied.ToastSize);Assert.Equal(60,applied.ToastTransparency);
        Assert.DoesNotContain("Toast",EnginePreferences.From(reopened).Fingerprint);
    }

    [Fact]
    public Task StartingWithToastsDisabledDoesNotShowOrReplaySavedNotifications()=>Sta(async()=>
    {
        var store=new SettingsStore(_directory);store.Save(new(){ToastEnabled=false,ToastSize=70,ToastTransparency=60});
        using var log=new AppLog(Path.Combine(_directory,"logs"));await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        await using var presenter=new NoticePresenter(vm);
        SwitchNotice Notice(int run)=>new("채널 2 전환",[new("Keyboard",true,"키보드","k",DeviceNoticeState.Confirmed)],run);
        presenter.Show(Notice(1));presenter.FlushForFixture();var initiallyOff=await presenter.ProbeAsync();Assert.False(initiallyOff.Visible);Assert.Equal(0,initiallyOff.WindowCount);
        vm.ToastEnabled=true;presenter.FlushForFixture();var restored=await presenter.ProbeAsync();Assert.False(restored.Visible);Assert.True(restored.WindowCount>0);
        presenter.Show(Notice(2));presenter.FlushForFixture();var shown=await presenter.ProbeAsync();
        Assert.True(shown.Visible);Assert.Equal(70,shown.Size);Assert.Equal(.4,shown.Opacity,8);
    });

    [Fact]
    public Task LiveToastChangesApplyWithoutReplayingDisabledNotifications()=>Sta(async()=>
    {
        var store=new SettingsStore(_directory);using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        await using var presenter=new NoticePresenter(vm);
        SwitchNotice Notice(int run)=>new("채널 2 전환",[new("MX Mechanical",null,"키보드","k",DeviceNoticeState.SignalObserved)],run);
        var connection=new SwitchNotice("기기 연결됨",[new("MX Master 4",null,"마우스","m",DeviceNoticeState.Connected)],1);
        async Task<NoticePresenter.PresentationProbe> Probe(){presenter.FlushForFixture();return await presenter.ProbeAsync();}
        presenter.Show(Notice(1));var normal=await Probe();Assert.True(normal.Visible);Assert.NotNull(normal.Bounds);Assert.Equal(1,normal.Opacity);Assert.True(normal.WindowCount>0);
        vm.ToastSize=50;var small=await Probe();Assert.True(small.Visible);
        Assert.InRange(Math.Abs(small.Bounds!.Value.Width-normal.Bounds!.Value.Width/2),0,1);
        Assert.InRange(Math.Abs(small.Bounds.Value.Height-normal.Bounds.Value.Height/2),0,1);
        Assert.InRange(Math.Abs(small.Bounds.Value.Bottom-normal.Bounds.Value.Bottom),0,1);
        vm.ToastSize=200;var large=await Probe();Assert.InRange(Math.Abs(large.Bounds!.Value.Width-normal.Bounds.Value.Width*2),0,2);
        vm.ToastTransparency=75;Assert.Equal(.25,(await Probe()).Opacity);
        vm.ToastTransparency=100;Assert.False((await Probe()).Visible);
        vm.ToastTransparency=30;Assert.True((await Probe()).Visible);Assert.Equal(.7,(await Probe()).Opacity,8);
        presenter.ShowConnection(connection);Assert.True((await Probe()).ConnectionVisible);
        vm.ToastEnabled=false;var off=await Probe();Assert.False(off.Visible);Assert.False(off.ConnectionVisible);Assert.Null(off.Run);Assert.Equal(0,off.WindowCount);
        presenter.Show(Notice(2));presenter.ShowConnection(connection);off=await Probe();Assert.False(off.Visible);Assert.False(off.ConnectionVisible);
        vm.ToastEnabled=true;var onAgain=await Probe();Assert.False(onAgain.Visible);Assert.False(onAgain.ConnectionVisible);Assert.True(onAgain.WindowCount>0);
        presenter.Show(Notice(2));Assert.False((await Probe()).Visible);
        presenter.Show(Notice(3));Assert.True((await Probe()).Visible);
        vm.ToastEnabled=false;
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
