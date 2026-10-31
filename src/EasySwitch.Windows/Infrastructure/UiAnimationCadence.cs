using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EasySwitch.Windows.Infrastructure;

// Give short WPF control animations a display-rate wake-up when the natural
// rendering pass is late. The WPF animation clock still owns values and easing.
internal sealed class UiAnimationCadence
{
    private static readonly ConditionalWeakTable<Dispatcher,UiAnimationCadence> Instances=new();
    private static int _hoverInstalled;
    private readonly AnimationFrameClock _clock;
    private WeakReference<UIElement>? _root;
    private long _until,_lastRender;
    private bool _active;
    private UiAnimationCadence(Dispatcher dispatcher)
    {
        _clock=new(dispatcher,note=>System.Diagnostics.Trace.WriteLine(note));_clock.Tick+=Frame;
        dispatcher.ShutdownStarted+=(_,_)=>{Stop();_clock.Dispose();};
    }
    internal static void ObserveButtonHover()
    {
        if(Interlocked.Exchange(ref _hoverInstalled,1)!=0)return;
        MouseEventHandler hover=(sender,_)=>{if(sender is FrameworkElement element)Request(element,100);};
        EventManager.RegisterClassHandler(typeof(ButtonBase),UIElement.MouseEnterEvent,hover,true);
        EventManager.RegisterClassHandler(typeof(ButtonBase),UIElement.MouseLeaveEvent,hover,true);
    }
    internal static void Request(FrameworkElement element,double milliseconds)
    {
        if(!element.IsLoaded || element.Dispatcher.HasShutdownStarted || PresentationSource.FromVisual(element) is not HwndSource source || source.RootVisual is not UIElement{IsVisible:true} root)return;
        var pulse=Instances.GetValue(element.Dispatcher,d=>new(d));
        pulse._root=new(root);pulse._until=Math.Max(pulse._until,Stopwatch.GetTimestamp()+(long)((milliseconds+30)*Stopwatch.Frequency/1000));
        pulse._clock.SetMonitor(System.Windows.Forms.Screen.FromHandle(source.Handle).DeviceName);
        if(pulse._active)return;
        pulse._active=true;pulse._lastRender=0;CompositionTarget.Rendering+=pulse.Rendered;pulse._clock.Start();
    }
    private void Rendered(object? sender,EventArgs e)=>_lastRender=Stopwatch.GetTimestamp();
    private void Frame(object? sender,EventArgs e)
    {
        var now=Stopwatch.GetTimestamp();
        if(now>_until || _root is null || !_root.TryGetTarget(out var root) || !root.IsVisible){Stop();return;}
        if(_lastRender==0 || (now-_lastRender)*1000d/Stopwatch.Frequency>1000/_clock.Rate*.8)root.InvalidateVisual();
    }
    private void Stop()
    {
        if(!_active)return;_active=false;_clock.Stop();CompositionTarget.Rendering-=Rendered;_root=null;
    }
}
