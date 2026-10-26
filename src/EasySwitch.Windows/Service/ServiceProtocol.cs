using System.Buffers.Binary;
using System.Text.Json;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Service;

internal sealed record EnginePreferences(bool FollowEnabled,string? KeyboardId,string? KeyboardName,string? MouseId,string? MouseName,
    string[]? SelectedDeviceIds,DeviceInfoCache[] SelectedDeviceNames,string Language,string[] ChannelLabels,long Revision=0)
{
    public static EnginePreferences From(AppSettings s)=>new(s.FollowEnabled,s.KeyboardId,s.KeyboardName,s.MouseId,s.MouseName,s.SelectedDeviceIds,s.SelectedDeviceNames,s.Language,s.ChannelLabels,s.EngineSettingsRevision);
    public AppSettings Apply(AppSettings s)=>s with{FollowEnabled=FollowEnabled,KeyboardId=KeyboardId,KeyboardName=KeyboardName,MouseId=MouseId,MouseName=MouseName,
        SelectedDeviceIds=SelectedDeviceIds,SelectedDeviceNames=SelectedDeviceNames,Language=Language,ChannelLabels=ChannelLabels,EngineSettingsRevision=Revision};
    [System.Text.Json.Serialization.JsonIgnore] public string Fingerprint=>JsonSerializer.Serialize(this);
    [System.Text.Json.Serialization.JsonIgnore] public string ContentFingerprint=>JsonSerializer.Serialize(this with{Revision=0});
    [System.Text.Json.Serialization.JsonIgnore] public bool HasSelectionPreference=>SelectedDeviceIds is not null || !string.IsNullOrEmpty(KeyboardId) || !string.IsNullOrEmpty(MouseId);
    public static long NextRevision(long previous)=>Math.Min(DateTime.MaxValue.Ticks,Math.Max(DateTime.UtcNow.Ticks,previous+1));
    public bool PreferRestoredOver(EnginePreferences service)=>HasSelectionPreference &&
        (Revision>service.Revision || Revision==service.Revision && ContentFingerprint!=service.ContentFingerprint);
    public void Validate()
    {
        if(Revision<0 || Revision>DateTime.MaxValue.Ticks)throw new InvalidDataException("잘못된 서비스 설정 저장 순서");
        if(SelectedDeviceNames is null || ChannelLabels is null || (SelectedDeviceIds?.Length??0)>32 || SelectedDeviceNames.Length>32 || SelectedDeviceNames.Any(d=>d is null) || ChannelLabels.Length!=3 || Language is not ("ko" or "en"))throw new InvalidDataException("잘못된 서비스 설정");
        var strings=(SelectedDeviceIds??[]).Concat(SelectedDeviceNames.SelectMany(d=>new[]{d.Id,d.Name,d.Kind})).Concat(ChannelLabels).Concat(new[]{KeyboardId??"",MouseId??"",KeyboardName??"",MouseName??""});
        if(strings.Any(s=>s is null || s.Length>512))throw new InvalidDataException("서비스 설정의 항목 길이가 너무 김");
    }
}
internal sealed record ServiceRequest(string Command="State",string? Epoch=null,long After=0,EnginePreferences? Preferences=null,int Target=-1,string Reason="",long ExpiresAt=0,bool Codes=false,long Offset=0,long PowerGeneration=0);
internal sealed record EngineEvent(long Sequence,DateTimeOffset At,LogEntry? Log=null,CodeLogEntry? Code=null,SwitchNotice? Notice=null,bool Connection=false,int? KeyboardTarget=null);
internal sealed record ServiceSnapshot(string Epoch,long Sequence,DeviceInfo[] Devices,EnginePreferences Preferences,int? LastDestination,string LastRequest,EngineEvent[] Events,bool HistoryTruncated=false,long PowerGeneration=0)
{
    // Local receive stamps never cross the service wire or survive a connection.
    [System.Text.Json.Serialization.JsonIgnore] public long ClientConnection{get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public long ClientRevision{get;init;}
}
internal sealed record ServiceReply(ServiceSnapshot? State=null,string? Error=null,byte[]? Data=null,long Length=0,string? LogEpoch=null);

internal static class ServiceWire
{
    internal const int MaximumBytes=2*1024*1024;
    public static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);
        if(bytes.Length>MaximumBytes)throw new InvalidDataException("서비스 메시지가 너무 큼");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,token);await stream.WriteAsync(bytes,token);await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <=0 or >MaximumBytes)throw new InvalidDataException("잘못된 서비스 메시지 길이");
        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,token);
        return JsonSerializer.Deserialize<T>(bytes)??throw new InvalidDataException("비어 있는 서비스 메시지");
    }
}

internal sealed class EngineJournal
{
    private readonly object _gate=new();private readonly Queue<EngineEvent> _events=[];private long _sequence;
    public string Epoch{get;}=Guid.NewGuid().ToString("N");
    public void Add(LogEntry log){lock(_gate)Append(new(++_sequence,log.At,Log:log));}
    public void Add(CodeLogEntry code){lock(_gate)Append(new(++_sequence,code.At,Code:code));}
    public void Add(SwitchNotice notice,bool connection){lock(_gate)Append(new(++_sequence,DateTimeOffset.Now,Notice:notice,Connection:connection));}
    public void AddKeyboardSwitch(int target){lock(_gate)Append(new(++_sequence,DateTimeOffset.Now,KeyboardTarget:target));}
    private void Append(EngineEvent item){_events.Enqueue(item);while(_events.Count>1200)_events.Dequeue();}
    public (long Sequence,EngineEvent[] Events,bool Truncated) Read(string? epoch,long after)
    {
        lock(_gate)
        {
            var cursor=epoch==Epoch?after:0;
            var items=_events.Where(e=>e.Sequence>cursor).Take(300).ToArray();
            return(items.LastOrDefault()?.Sequence??_sequence,items,cursor>0 && _events.TryPeek(out var first) && cursor<first.Sequence-1);
        }
    }
}
