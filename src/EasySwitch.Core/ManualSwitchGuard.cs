namespace EasySwitch.Core;

// A departure may be the echo of an in-progress app request. Wall-clock suppression also
// swallowed a real new departure immediately after the keyboard returned to this PC.
public sealed class ManualSwitchGuard
{
    private readonly object _gate=new();
    private (long Generation,int Target)? _active;
    public void Begin(long generation,int target){lock(_gate)_active=(generation,target);}
    public bool Matches(long generation,int target){lock(_gate)return _active==(generation,target);}
    public void Complete(long generation){lock(_gate)if(_active?.Generation==generation)_active=null;}
}
