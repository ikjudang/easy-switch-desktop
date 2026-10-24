namespace EasySwitch.Windows.Hardware;

internal enum DiscoveryResult { Retry, Ready, Absent }

internal sealed class DiscoveryPollSchedule
{
    private readonly long _started;
    private long _next;
    public DiscoveryPollSchedule(long started){_started=started;_next=started;}
    public bool IsDue(long now)=>now>=_next;
    public void Complete(long now,DiscoveryResult result)
    {
        // A silent registered device is not an empty receiver slot. Only explicit absence
        // or unsupported-feature replies use the slow discovery interval.
        var delay=result==DiscoveryResult.Absent?30_000:result==DiscoveryResult.Retry && now-_started<15_000?100:3_000;
        _next=now+delay;
    }
}
