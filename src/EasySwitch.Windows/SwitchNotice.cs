using EasySwitch.Core;

namespace EasySwitch.Windows;

public enum DeviceNoticeState { Unconfirmed, Waiting, Switching, Confirmed, Departed, Failed, Unavailable, Cancelled, SignalObserved, WaitExpired, Connected, CommandSent }
public sealed record DeviceResult(string Name,bool? Success,string Kind="",string Id="",DeviceNoticeState State=DeviceNoticeState.Unconfirmed)
{
    // O means the observed local step completed; the adjacent status says which step. It must
    // not change Success (destination confirmation) or the outcome recorded by the switch engine.
    internal bool? Indicator => Success ?? State switch
    {
        DeviceNoticeState.Confirmed or DeviceNoticeState.Departed or DeviceNoticeState.SignalObserved or DeviceNoticeState.Connected => true,
        DeviceNoticeState.Failed or DeviceNoticeState.Cancelled or DeviceNoticeState.WaitExpired => false,
        _ => null,
    };
    public static DeviceResult? FromProgress(string id,string name,string kind,SwitchProgress progress)=>progress.Step==SwitchStep.Written
        ?new(name,null,kind,id,DeviceNoticeState.CommandSent):null;
    public static DeviceResult FromOutcome(string id,string name,string kind,SwitchOutcome outcome)=>new(name,
        outcome is SwitchOutcome.AlreadyCurrent or SwitchOutcome.TargetReported?true:outcome is SwitchOutcome.Failed or SwitchOutcome.StillOnSource?false:null,
        kind,id,outcome switch{SwitchOutcome.AlreadyCurrent or SwitchOutcome.TargetReported=>DeviceNoticeState.Confirmed,SwitchOutcome.LocalLinkClosed=>DeviceNoticeState.Departed,SwitchOutcome.Failed or SwitchOutcome.StillOnSource=>DeviceNoticeState.Failed,SwitchOutcome.Cancelled=>DeviceNoticeState.Cancelled,_=>DeviceNoticeState.Unconfirmed});
}
public sealed record SwitchNotice(string Title,IReadOnlyList<DeviceResult> Devices,int RunId=0)
{
    public static SwitchNotice Merge(SwitchNotice? previous,SwitchNotice next)
    {
        if(previous is null || next.RunId>previous.RunId)return next;
        if(next.RunId<previous.RunId)return previous;
        static string Key(DeviceResult item)=>item.Id.Length>0?item.Id:item.Kind+":"+item.Name;
        static int PendingRank(DeviceNoticeState state)=>state switch{DeviceNoticeState.Waiting=>0,DeviceNoticeState.Switching=>1,DeviceNoticeState.CommandSent=>2,_=>-1};
        var rows=previous.Devices.ToList();
        foreach(var incoming in next.Devices)
        {
            var index=rows.FindIndex(row=>Key(row)==Key(incoming));
            if(index<0)rows.Add(incoming);
            else if(PendingRank(incoming.State)<0 || (PendingRank(rows[index].State)>=0 && PendingRank(incoming.State)>=PendingRank(rows[index].State)))rows[index]=incoming;
        }
        return next with{Devices=rows};
    }
}
