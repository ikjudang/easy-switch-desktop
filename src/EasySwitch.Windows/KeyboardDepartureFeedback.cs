using System.Runtime.CompilerServices;
using EasySwitch.Windows.Hardware;

namespace EasySwitch.Windows;

// Filters visual feedback only. The follow coordinator still handles the original signal.
internal sealed class KeyboardDepartureFeedback(Func<long>? clock=null)
{
    private sealed class LastDeparture{public long Generation=long.MinValue;public int Target=-1;public long At;}
    private readonly Func<long> _now=clock??(()=>Environment.TickCount64);
    private readonly object _gate=new();
    private readonly ConditionalWeakTable<HostDevice,LastDeparture> _last=new();
    public bool TryAccept(HostDevice keyboard,int target)
    {
        if(target is <0 or >2 || target==keyboard.CurrentHost)return false;
        lock(_gate)
        {
            var last=_last.GetValue(keyboard,_=>new());var generation=keyboard.ConnectionGeneration;var now=_now();
            if(last.Generation==generation && last.Target==target && now-last.At<250)return false;
            last.Generation=generation;last.Target=target;last.At=now;return true;
        }
    }
    public void Arrived(HostDevice keyboard){lock(_gate)_last.Remove(keyboard);}
}
