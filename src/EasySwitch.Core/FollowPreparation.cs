namespace EasySwitch.Core;

public enum FollowReadiness { Wait, Ready, Exclude }

public static class FollowPreparation
{
    public const long WaitMilliseconds=10_000;
    public static bool Expired(long started,long now)=>now-started>WaitMilliseconds;
    public static FollowReadiness Readiness(bool exists,bool transport,bool? localLink,bool online,int hosts,bool observedLocally)
    {
        if(localLink==false && !observedLocally)return FollowReadiness.Exclude;
        return exists && transport && localLink!=false && online && hosts>0?FollowReadiness.Ready:FollowReadiness.Wait;
    }
}
