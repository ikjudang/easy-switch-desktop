using System.Text.Json;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Hardware;

// Durable hints only. Live handles, online state and the current host are never persisted.
internal sealed record DeviceRoute(string Id,string Name,string Kind,string Connection,int HostCount,int FeatureIndex,int? WirelessProductId=null,string? TransportKey=null,int? DeviceIndex=null)
{
    internal bool Valid=>Id is {Length:64} && Id.All(Uri.IsHexDigit) && Name is {Length:>0 and <=512} &&
        Kind is "키보드" or "마우스" && Connection is {Length:<=128} && HostCount is >=1 and <=3 &&
        FeatureIndex is >=1 and <=255 && WirelessProductId is null or >=0 and <=65535 &&
        (TransportKey is null && DeviceIndex is null || TransportKey is {Length:>0 and <=4096} && DeviceIndex is >=1 and <=255 &&
         Id==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{TransportKey}#{DeviceIndex}"))));
    internal static DeviceRoute? From(DeviceInfo info,int? wirelessProductId=null,string? transportKey=null,int? deviceIndex=null)
    {
        var route=new DeviceRoute(info.Id,info.Name,info.Kind,info.Connection,info.HostCount,info.FeatureIndex,wirelessProductId,transportKey,deviceIndex);
        return route.Valid?route:null;
    }
}
internal enum DeviceRestoreStage { WaitingForTransport,Checking,Retrying,Ready,Invalidated }
internal sealed record DeviceRestoreStatus(string Id,DeviceRestoreStage Stage,string Reason);

internal sealed class DeviceRouteCache
{
    private sealed record Document(int Version,DeviceRoute[] Routes);
    private readonly string _path;
    private readonly AppLog _log;
    private readonly object _gate=new();
    private readonly Dictionary<string,DeviceRoute> _routes=new(StringComparer.Ordinal);
    private readonly Dictionary<string,DeviceRestoreStatus> _states=new(StringComparer.Ordinal);
    private HashSet<string> _expected=[];
    private Task _write=Task.CompletedTask;
    private bool _dirty;
    private int _revision;
    internal DeviceRouteCache(string directory,AppLog log)
    {
        _path=Path.Combine(directory,"device-routes.json");_log=log;
        try
        {
            if(!File.Exists(_path))return;
            if(new FileInfo(_path).Length>1_048_576)throw new InvalidDataException("기기 복원 캐시가 너무 큼");
            var document=JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if(document is not {Version:1} || document.Routes is null || document.Routes.Length>128)
                throw new InvalidDataException("지원하지 않는 기기 복원 캐시 형식");
            foreach(var route in document.Routes)
            {
                if(route is null || !route.Valid)throw new InvalidDataException("잘못된 기기 복원 캐시 항목");
                _routes[route.Id]=route;
            }
            _log.Write($"기기 복원 캐시 읽음: {_routes.Count}개. 실제 응답 전에는 연결됨으로 처리하지 않음");
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {_routes.Clear();_log.Write("기기 복원 캐시 읽기 실패: "+error.Message+". 잘못된 캐시는 제어에 사용하지 않고 새로 검색함");}
    }
    internal DeviceRoute? Find(string id){lock(_gate)return _routes.GetValueOrDefault(id);}
    internal DeviceRestoreStatus[] Status{get{lock(_gate)return _expected.Select(id=>_states.GetValueOrDefault(id)??new(id,DeviceRestoreStage.WaitingForTransport,"통로 검색 대기")).ToArray();}}
    internal void Expect(IEnumerable<string> ids)
    {
        lock(_gate)
        {
            var next=ids.ToHashSet(StringComparer.Ordinal);if(_expected.SetEquals(next))return;
            _expected=next;
            foreach(var id in next)if(!_states.ContainsKey(id))SetStageLocked(id,DeviceRestoreStage.WaitingForTransport,"선택한 기기의 통로 검색 대기");
        }
    }
    internal void Stage(string id,DeviceRestoreStage stage,string reason)
    {lock(_gate)SetStageLocked(id,stage,reason);}
    private void SetStageLocked(string id,DeviceRestoreStage stage,string reason)
    {
        if(_states.TryGetValue(id,out var previous) && previous.Stage==stage)return;
        _states[id]=new(id,stage,reason);
        if(_expected.Contains(id))_log.Write($"기기 복원 {id[..Math.Min(12,id.Length)]}: {stage} · {reason}");
        if(_expected.Count>0 && _expected.All(key=>_states.TryGetValue(key,out var state) && state.Stage==DeviceRestoreStage.Ready))
            _log.Write($"선택 기기 제어 복원 완료: {_expected.Count}/{_expected.Count}개. 현재 통로의 실제 응답을 확인함");
    }
    internal void Remember(DeviceInfo info,int? wirelessProductId,string? transportKey=null,int? deviceIndex=null)
    {
        if(!info.Online || DeviceRoute.From(info,wirelessProductId,transportKey,deviceIndex) is not {} route)return;
        lock(_gate)
        {
            if(_routes.GetValueOrDefault(route.Id)==route && (!_dirty || !_write.IsCompleted))return;
            _routes[route.Id]=route;
            if(_routes.Count>128)_routes.Remove(_routes.Keys.FirstOrDefault(key=>key!=route.Id && !_expected.Contains(key))??route.Id);
            QueueWriteLocked();
        }
    }
    internal void Invalidate(string id)
    {
        lock(_gate)
        {
            SetStageLocked(id,DeviceRestoreStage.Invalidated,"기존 수신기 등록과 현재 기기가 달라 캐시 폐기");
            if(_routes.Remove(id))QueueWriteLocked();
        }
    }
    internal int Import(IEnumerable<DeviceInfo> devices)
    {
        var added=0;
        lock(_gate)
        {
            foreach(var info in devices)
                if(info.Online && DeviceRoute.From(info) is {} route && !_routes.ContainsKey(route.Id) && _routes.Count<128)
                {_routes.Add(route.Id,route);added++;}
            if(added>0)QueueWriteLocked();
        }
        return added;
    }
    private void QueueWriteLocked()
    {
        _dirty=true;
        var revision=++_revision;var routes=_routes.Values.ToArray();
        _write=_write.ContinueWith(_=>
        {
            try
            {
                lock(_gate)if(revision!=_revision)return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary=_path+"."+Environment.ProcessId+".tmp";
                File.WriteAllText(temporary,JsonSerializer.Serialize(new Document(1,routes),new JsonSerializerOptions{WriteIndented=true}));
                File.Move(temporary,_path,true);
                lock(_gate)if(revision==_revision)_dirty=false;
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {_log.Write("기기 복원 캐시 저장 실패: "+error.Message+". 기기 선택과 현재 통신은 유지함");}
        },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
    }
    internal Task FlushAsync(){lock(_gate)return _write;}
}
