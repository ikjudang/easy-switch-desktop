using EasySwitch.Windows.Hardware;

namespace EasySwitch.Windows;

internal sealed class LocalConnectionNotices
{
    private readonly Dictionary<string,(object Connection,long Generation)> _reported=[];
    private readonly object _gate=new();

    public DeviceResult? Observe(DeviceInfo device,object connection,long generation)
    {
        if(!device.Online || device.HostCount==0 || device.CurrentHost is null || device.Kind is not ("키보드" or "마우스"))return null;
        lock(_gate)
        {
            if(_reported.TryGetValue(device.Id,out var previous) && ReferenceEquals(previous.Connection,connection) && previous.Generation==generation)return null;
            _reported[device.Id]=(connection,generation);
        }
        return new(device.Name,null,device.Kind,device.Id,DeviceNoticeState.Connected);
    }
}
