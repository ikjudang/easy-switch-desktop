using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using EasySwitch.Core;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class UiReliabilityTests : IDisposable
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));

    [Fact]
    public Task FollowRecheckRunsOnlyWhileAFollowWaitsAndKeepsTheTenSecondExpiry()=>Sta(async()=>
    {
        var store=new SettingsStore(_directory);store.Save(new AppSettings{FollowEnabled=true,SelectedDeviceIds=["mouse"],SelectedDeviceNames=[new("mouse","MX Master","마우스")]});
        using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
        var timer=(DispatcherTimer)typeof(MainViewModel).GetField("_followExpiry",Hidden)!.GetValue(vm)!;
        void Arm()=>typeof(MainViewModel).GetMethod("ArmFollowExpiry",Hidden)!.Invoke(vm,null);
        // Stands in for Start() without opening this PC's HID devices.
        typeof(MainViewModel).GetField("_followExpiryAllowed",Hidden)!.SetValue(vm,true);
        Arm();Assert.False(timer.IsEnabled);

        // A follow that started 9.3 s ago waits for a mouse that has no local link yet.
        SetPendingFollow(vm,901,Environment.TickCount64-(FollowPreparation.WaitMilliseconds-700));
        Arm();Assert.True(timer.IsEnabled);
        await Task.Delay(300);Assert.True(timer.IsEnabled);Assert.NotNull(PendingFollow(vm));
        var clock=System.Diagnostics.Stopwatch.StartNew();
        while(timer.IsEnabled && clock.ElapsedMilliseconds<3000)await Task.Delay(20);
        Assert.False(timer.IsEnabled);Assert.Null(PendingFollow(vm));
        Assert.Contains(log.RecentCodes(),entry=>entry.Code.Contains("|R0901|F2|ALL|CAN_EXP|",StringComparison.Ordinal));
    });

    [Fact]
    public Task CancelledFollowCodesComeFromTheCallerNotFromReasonWording()=>Sta(async()=>
    {
        var store=new SettingsStore(_directory);store.Save(new AppSettings{FollowEnabled=true,SelectedDeviceIds=["mouse"],SelectedDeviceNames=[new("mouse","MX Master","마우스")]});
        using var log=new AppLog(Path.Combine(_directory,"logs"));
        var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
        var odd=new DeviceInfo("odd","직접 종료 로컬 링크 Mouse","Bluetooth","마우스",true,3,0,9);
        vm.UsePreviewDevices([odd]);
        string Code(int id)=>log.RecentCodes().Select(entry=>entry.Code).Single(code=>code.Contains($"|R{id:D4}|",StringComparison.Ordinal) && code.Contains("|CAN_",StringComparison.Ordinal));

        // A device name inside the log reason used to turn a selection change into CAN_RET.
        SetPendingFollow(vm,911);vm.ToggleDevice(odd);
        Assert.Contains("|CAN_CFG|",Code(911));
        SetPendingFollow(vm,912);await vm.ChangePowerAsync(false,"실행 공백 감지");
        Assert.Contains("|CAN_PWR|",Code(912));
        SetPendingFollow(vm,913);vm.BlockForSuspend("Windows 절전 진입");
        Assert.Contains("|CAN_PWR|",Code(913));
        SetPendingFollow(vm,914);await vm.DisposeAsync();
        Assert.Contains("|CAN_STOP|",Code(914));
    });

    [Fact]
    public void SwitchSourcesKeepTheirServiceReasonsAndCodeLetters()
    {
        // The reasons are the service request field; older service and UI builds send the same text.
        Assert.Equal(("홈 채널 버튼","채널 버튼","트레이 채널 버튼","플로팅 채널 버튼"),(SwitchRequestSource.HomeTile,SwitchRequestSource.Command,SwitchRequestSource.Tray,SwitchRequestSource.Floating));
        Assert.Equal("T",SwitchRequestSource.Code(SwitchRequestSource.Tray));
        Assert.Equal("P",SwitchRequestSource.Code(SwitchRequestSource.Floating));
        Assert.Equal("B",SwitchRequestSource.Code(SwitchRequestSource.HomeTile));
        Assert.Equal("B",SwitchRequestSource.Code(SwitchRequestSource.Command));
        Assert.Equal("B",SwitchRequestSource.Code("fixture"));
    }

    [Fact]
    public async Task FireAndForgetFailuresReachTheLogWithoutTouchingSuccessOrCancellation()
    {
        using var log=new AppLog(Path.Combine(_directory,"logs"));
        Task.FromException(new InvalidOperationException("fixture failure")).LogFaults(log,"즉시 실패 작업");
        Task.FromCanceled(new CancellationToken(true)).LogFaults(log,"취소 작업");
        Task.CompletedTask.LogFaults(log,"완료 작업");
        var later=new TaskCompletionSource();later.Task.LogFaults(log,"지연 작업");later.SetException(new IOException("later failure"));
        await log.FlushAsync();
        var lines=log.Recent().Select(entry=>entry.Message).ToArray();
        Assert.Contains(lines,line=>line.StartsWith("즉시 실패 작업 실패:",StringComparison.Ordinal) && line.Contains("fixture failure",StringComparison.Ordinal));
        Assert.Contains(lines,line=>line.StartsWith("지연 작업 실패:",StringComparison.Ordinal) && line.Contains("later failure",StringComparison.Ordinal));
        Assert.DoesNotContain(lines,line=>line.StartsWith("취소 작업",StringComparison.Ordinal) || line.StartsWith("완료 작업",StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorDialogsDoNotStackOrRepeat()
    {
        static Exception Thrown(Func<Exception> make){try{throw make();}catch(Exception error){return error;}}
        var gate=new ErrorDialogGate();
        Assert.True(gate.TryEnter(Thrown(()=>new InvalidOperationException("first"))));
        // A failure raised while the dialog's message loop runs is only logged.
        Assert.False(gate.TryEnter(Thrown(()=>new IOException("nested"))));
        gate.Exit();
        Assert.False(gate.TryEnter(Thrown(()=>new InvalidOperationException("same kind again"))));
        Assert.True(gate.TryEnter(Thrown(()=>new IOException("a different failure later"))));
        gate.Exit();
    }

    [Fact]
    public async Task ABusyClipboardIsRetriedAndAFinalFailureIsLoggedInsteadOfRaised()
    {
        var calls=0;var logs=new List<string>();
        var busy=new COMException("clipboard busy",unchecked((int)0x800401D0));
        Assert.True(await MainWindow.CopyWithRetryAsync("lines",_=>{if(++calls<3)throw busy;},logs.Add,4,1));
        Assert.Equal(3,calls);Assert.Empty(logs);
        calls=0;
        Assert.False(await MainWindow.CopyWithRetryAsync("lines",_=>{calls++;throw busy;},logs.Add,4,1));
        Assert.Equal(4,calls);Assert.Single(logs);
    }

    [Fact]
    public async Task LogFolderLabelFollowsTheLanguage()
    {
        var store=new SettingsStore(_directory);using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
        Assert.Equal("로컬 폴더에 기록 중",vm.ExportLabel);
        vm.SetLanguage("en");Assert.Equal("Saving to the local folder",vm.ExportLabel);
    }

    private static void SetPendingFollow(MainViewModel vm,int id,long? at=null)
    {
        var code=Activator.CreateInstance(typeof(MainViewModel).GetNestedType("CodeRun",BindingFlags.NonPublic)!,id,"F",1)!;
        var intent=Activator.CreateInstance(typeof(MainViewModel).GetNestedType("FollowIntent",BindingFlags.NonPublic)!,new object?[]{new[]{"mouse"},1,at??Environment.TickCount64,code,null})!;
        typeof(MainViewModel).GetField("_pendingFollow",Hidden)!.SetValue(vm,intent);
    }
    private static object? PendingFollow(MainViewModel vm)=>typeof(MainViewModel).GetField("_pendingFollow",Hidden)!.GetValue(vm);
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
