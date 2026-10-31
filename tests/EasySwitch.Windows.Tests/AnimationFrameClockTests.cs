using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class AnimationFrameClockTests
{
    [Fact]
    public Task BusyDispatcherDoesNotAccumulateFramesAndStopCancelsTheQueuedGeneration()=>Sta(()=>
    {
        var dispatcher=Dispatcher.CurrentDispatcher;using var posted=new ManualResetEventSlim();
        var operations=0;var ticks=0;using var clock=new AnimationFrameClock(dispatcher,_=>{});
        dispatcher.Hooks.OperationPosted+=(_,e)=>{if(e.Operation.Priority==DispatcherPriority.Render){Interlocked.Increment(ref operations);posted.Set();}};
        clock.Tick+=(_,_)=>ticks++;
        clock.Start();Assert.True(posted.Wait(2000));Thread.Sleep(100);Assert.Equal(1,Volatile.Read(ref operations));
        clock.Stop();dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Assert.Equal(0,ticks);
        var frame=new DispatcherFrame();
        var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timeout.Tick+=(_,_)=>frame.Continue=false;timeout.Start();
        clock.Tick+=(_,_)=>{if(ticks==4){clock.Stop();frame.Continue=false;}};clock.Start();Dispatcher.PushFrame(frame);timeout.Stop();
        Assert.Equal(4,ticks);Thread.Sleep(100);dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Assert.Equal(4,ticks);
        clock.Dispose();clock.Dispose();
    });

    [Fact]
    public Task LongFrameDoesNotQueueCatchUpCallbacksWhileItRuns()=>Sta(()=>
    {
        var dispatcher=Dispatcher.CurrentDispatcher;var posted=0;using var clock=new AnimationFrameClock(dispatcher,_=>{});
        dispatcher.Hooks.OperationPosted+=(_,e)=>{if(e.Operation.Priority==DispatcherPriority.Render)Interlocked.Increment(ref posted);};
        var frame=new DispatcherFrame();var frames=0;
        clock.Tick+=(_,_)=>{frames++;Thread.Sleep(100);Assert.Equal(1,Volatile.Read(ref posted));clock.Stop();frame.Continue=false;};
        var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timeout.Tick+=(_,_)=>frame.Continue=false;timeout.Start();
        clock.Start();Dispatcher.PushFrame(frame);timeout.Stop();Assert.Equal(1,frames);
    });
    private static Task Sta(Action action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
}
