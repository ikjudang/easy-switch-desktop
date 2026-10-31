using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace EasySwitch.Windows.Infrastructure;

// A deadline clock, independent of the duration of the preceding UI frame.
// It posts at most one outstanding callback and does no work while stopped.
internal sealed class AnimationFrameClock:IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AutoResetEvent _changed=new(false);
    private readonly Thread _worker;
    private readonly Action<string> _report;
    private int _running,_generation,_pending,_disposed;
    private double _rate=60;
    private string? _monitor;
    private DispatcherTimer? _fallback;
    public event EventHandler? Tick;
    internal double Rate=>Volatile.Read(ref _rate);
    public AnimationFrameClock(Dispatcher dispatcher,Action<string> report)
    {
        _dispatcher=dispatcher;_report=report;
        _worker=new Thread(Run){IsBackground=true,Name="EasySwitch animation clock"};_worker.Start();
    }
    public void SetMonitor(string name,bool refresh=false)
    {
        _dispatcher.VerifyAccess();
        if(!refresh && _monitor==name)return;_monitor=name;
        var dc=CreateDC("DISPLAY",name,null,0);
        var rate=60;
        if(dc!=0){try{var found=GetDeviceCaps(dc,116);if(found>=24)rate=Math.Clamp(found,30,240);}finally{DeleteDC(dc);}}
        if(Math.Abs(Rate-rate)<.01)return;
        Volatile.Write(ref _rate,rate);if(_fallback is not null)_fallback.Interval=TimeSpan.FromSeconds(1d/rate);Interlocked.Increment(ref _generation);_changed.Set();
    }
    public void Start()
    {
        _dispatcher.VerifyAccess();if(Volatile.Read(ref _disposed)!=0 || Interlocked.Exchange(ref _running,1)!=0)return;
        Interlocked.Increment(ref _generation);if(_fallback is not null)_fallback.Start();else _changed.Set();
    }
    public void Stop()
    {
        _dispatcher.VerifyAccess();if(Volatile.Read(ref _disposed)!=0 || Interlocked.Exchange(ref _running,0)==0)return;
        Interlocked.Increment(ref _generation);_fallback?.Stop();_changed.Set();
    }
    private void Run()
    {
        try
        {
            using var timer=new DeadlineTimer(_report);var waits=new WaitHandle[]{_changed,timer};
            while(Volatile.Read(ref _disposed)==0)
            {
                if(Volatile.Read(ref _running)==0){_changed.WaitOne();continue;}
                var generation=Volatile.Read(ref _generation);var interval=Stopwatch.Frequency/Rate;var next=(double)Stopwatch.GetTimestamp();
                while(Volatile.Read(ref _disposed)==0 && Volatile.Read(ref _running)!=0 && generation==Volatile.Read(ref _generation))
                {
                    var remaining=next-Stopwatch.GetTimestamp();
                    if(remaining>0){timer.Arm(remaining/Stopwatch.Frequency);if(WaitHandle.WaitAny(waits)==0)break;}
                    if(Volatile.Read(ref _disposed)!=0 || Volatile.Read(ref _running)==0 || generation!=Volatile.Read(ref _generation))break;
                    if(Interlocked.CompareExchange(ref _pending,1,0)==0)
                    {
                        if(_dispatcher.HasShutdownStarted){Volatile.Write(ref _pending,0);return;}
                        _dispatcher.BeginInvoke(DispatcherPriority.Render,()=>
                        {
                            try{if(Volatile.Read(ref _disposed)==0 && Volatile.Read(ref _running)!=0 && generation==Volatile.Read(ref _generation))Tick?.Invoke(this,EventArgs.Empty);}
                            finally{Volatile.Write(ref _pending,0);}
                        });
                    }
                    next+=interval;
                    var now=Stopwatch.GetTimestamp();
                    if(next<=now)next+=(Math.Floor((now-next)/interval)+1)*interval;
                }
            }
        }
        catch(Exception error)
        {
            _report("애니메이션 프레임 시계 오류, 기본 시계로 복구: "+error.Message);
            if(!_dispatcher.HasShutdownStarted)_dispatcher.BeginInvoke(()=>
            {
                if(Volatile.Read(ref _disposed)!=0)return;
                _fallback=new(DispatcherPriority.Render){Interval=TimeSpan.FromSeconds(1/Rate)};
                _fallback.Tick+=(_,_)=>Tick?.Invoke(this,EventArgs.Empty);if(Volatile.Read(ref _running)!=0)_fallback.Start();
            });
        }
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        Volatile.Write(ref _running,0);Interlocked.Increment(ref _generation);_changed.Set();_fallback?.Stop();Tick=null;
        if(_worker.Join(1000))_changed.Dispose();else _report("애니메이션 프레임 시계 종료 대기 중");
    }
    private sealed class DeadlineTimer:WaitHandle
    {
        public DeadlineTimer(Action<string> report)
        {
            var handle=CreateWaitableTimerEx(0,null,2,0x001F0003);
            if(handle.IsInvalid){handle.Dispose();handle=CreateWaitableTimerEx(0,null,0,0x001F0003);report("고해상도 애니메이션 시계를 사용할 수 없어 기본 대기 시계를 사용함");}
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            SafeWaitHandle=handle;
        }
        public void Arm(double seconds)
        {
            var due=-(long)Math.Max(1,seconds*10_000_000);
            if(!SetWaitableTimer(SafeWaitHandle,ref due,0,0,0,false))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    [DllImport("kernel32.dll",EntryPoint="CreateWaitableTimerExW",SetLastError=true)]private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long due,int period,nint completion,nint argument,bool resume);
    [DllImport("gdi32.dll",EntryPoint="CreateDCW",CharSet=CharSet.Unicode)]private static extern nint CreateDC(string driver,string device,string? output,nint mode);
    [DllImport("gdi32.dll")]private static extern int GetDeviceCaps(nint dc,int capability);
    [DllImport("gdi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DeleteDC(nint dc);
}
