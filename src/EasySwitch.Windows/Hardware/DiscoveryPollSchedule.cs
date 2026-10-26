namespace EasySwitch.Windows.Hardware;

internal enum DiscoveryResult { Retry, Ready, Absent, Unsupported }

internal sealed class DiscoveryPollSchedule
{
    private readonly long _started;
    private long _next;
    public DiscoveryPollSchedule(long started){_started=started;_next=started;}
    public bool IsDue(long now)=>now>=_next;
    public void Complete(long now,DiscoveryResult result,bool expected=false)
    {
        // A silent registered device is not an empty receiver slot. Only explicit absence
        // or unsupported-feature replies use the slow discovery interval.
        var retry=result==DiscoveryResult.Retry || result==DiscoveryResult.Absent && expected;
        var delay=result==DiscoveryResult.Unsupported || result==DiscoveryResult.Absent && !expected?30_000:retry && now-_started<15_000?100:3_000;
        _next=now+delay;
    }
}
