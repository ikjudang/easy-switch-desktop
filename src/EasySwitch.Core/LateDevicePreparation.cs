namespace EasySwitch.Core;

/// <summary>
/// A channel request that finds a selected device not yet connected to this PC (for example a keyboard
/// still reattaching after its Easy-Switch key) waits for that device for a short time and sends the same
/// channel once it is ready. Same rule as the macOS engine (f937016): 3 s counted from the request, and a
/// device that is leaving right now is not waited for.
/// </summary>
public static class LateDevicePreparation
{
    public const long WaitMilliseconds=3_000;
    /// <summary>The wait is measured from the request, so time spent presenting the first notice counts.</summary>
    public static bool Expired(long requested,long now)=>now-requested>=WaitMilliseconds;
    /// <summary>A device whose departure was seen while its link is still up is going away; waiting for it would resend to a device that left.</summary>
    public static bool ShouldWait(bool enabled,bool departureSignal,bool transport,bool? localLink)=>enabled && !(departureSignal && transport && localLink!=false);
    /// <summary>
    /// Ready for the delayed write: the link is up, the channel feature and host count are known, restoration
    /// is done, and the device answered on its current link. A manual request that sends at once does not
    /// require the last answer; a device that only just arrived does.
    /// </summary>
    public static bool Ready(bool transport,bool? localLink,bool restoring,int featureIndex,int hosts,bool online)=>
        transport && localLink!=false && !restoring && featureIndex!=0 && hosts>0 && online;
}
