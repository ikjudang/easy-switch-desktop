using System.Security.Cryptography;
using System.Text;
using EasySwitch.Core;
using EasySwitch.Hidpp;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Hardware;

public sealed record DeviceInfo(string Id, string Name, string Connection, string Kind, bool Online, int HostCount, int? CurrentHost, int FeatureIndex)
{
    public string Status => Online ? $"연결됨 · 채널 {CurrentHost + 1}" : "연결 안 됨";
    public string Label => Id.Length == 0 ? Name : $"{Name}{(Connection.Length == 0 ? "" : $" · {Connection}")}{(Online ? "" : " · 연결 안 됨")}";
}

internal sealed class HostDevice : IHostDevice
{
    private readonly IDeviceTransport _connection;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly Func<long> _now;
    private readonly DiscoveryPollSchedule _poll;
    private DeviceRoute? _restoredRoute;
    private int _restorePending,_expected;
    private int _refreshRequested;
    private DeviceInfo _info;
    private bool _metadataRead;
    private int _localLink = -1;
    private long _generation = 1;
    private long _linkRevision;
    private long _onlineLinkRevision = -1;
    private readonly object _linkGate = new();
    private long _lastSwitchAt;
    private int _departureSignal;
    private int? _wirelessProductId;
    public int Index { get; }
    public string ConnectionKey => _connection.Key;
    internal bool UsesTransport(IDeviceTransport transport)=>ReferenceEquals(_connection,transport);
    public DeviceInfo Info
    {
        get
        {
            var info=Volatile.Read(ref _info);
            return info.Online && (Volatile.Read(ref _restorePending)!=0 || !LocalReplyIsCurrent(Interlocked.Read(ref _onlineLinkRevision)))?info with{Online=false}:info;
        }
    }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public int HostCount => Info.HostCount;
    public int? CurrentHost => Info.CurrentHost;
    public bool TransportAvailable => _connection.IsOpen;
    public bool? LocalLinkConnected => Volatile.Read(ref _localLink) switch { 0 => false, 1 => true, _ => null };
    public long ConnectionGeneration => Interlocked.Read(ref _generation)+(_connection.RecoveryRequired?1:0);
    internal bool TransportRecoveryRequired=>_connection.RecoveryRequired;
    internal bool IdentityReady=>Volatile.Read(ref _metadataRead);
    internal int? WirelessProductId=>_wirelessProductId;
    internal bool Restoring=>Volatile.Read(ref _restorePending)!=0;
    public bool SwitchGraceActive => Interlocked.Read(ref _lastSwitchAt) is var at && at != 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(at) < TimeSpan.FromSeconds(8);
    public bool DepartureSignalActive => Volatile.Read(ref _departureSignal) != 0 && SwitchGraceActive;
    public event Action<HostDevice>? Changed;
    public event Action<HostDevice>? FeatureReady;
    public event Action<HostDevice>? LinkArrived;
    public event Action<HostDevice>? PairingInvalidated;
    internal event Action<HostDevice>? RefreshCompleted;

    public HostDevice(IDeviceTransport connection, int index, AppLog log, DeviceInfo? cached = null,Func<long>? tickCount=null,DeviceRoute? restored=null)
    {
        _connection = connection;
        _log = log;
        Index = index;
        _now=tickCount??(()=>Environment.TickCount64);
        _poll=new(_now());
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{connection.Key}#{index}")));
        _info = new DeviceInfo(id, connection.IsReceiver ? $"{connection.Name} · 기기 {index}" : connection.Name,
            connection.ConnectionLabel, "기기", false, 0, null, 0);
        if (cached is not null && cached.Id == id && !connection.IsReceiver)
            _info = cached with { Online = false };
        if(restored is {Valid:true} && restored.Id==id)
        {
            _restoredRoute=restored;_restorePending=1;_wirelessProductId=restored.WirelessProductId;
            _info=_info with{Name=restored.Name,Kind=restored.Kind,HostCount=restored.HostCount,FeatureIndex=0,CurrentHost=null,Online=false};
            _log.Write($"[{Name}] 통신 정보 캐시 복원: 슬롯 {index}, 이전 feature 0x{restored.FeatureIndex:X2}. 실제 기능·기기 정보·채널 응답을 다시 확인함");
        }
    }

    internal void ExpectRestore(bool expected)
    {
        if(Interlocked.Exchange(ref _expected,expected?1:0)==0 && expected)Interlocked.Exchange(ref _refreshRequested,1);
    }

    public void NoteSwitchStarted() => Interlocked.Exchange(ref _lastSwitchAt, System.Diagnostics.Stopwatch.GetTimestamp());
    public void NoteDepartureSignal()
    {
        lock(_linkGate)
        {
            Interlocked.Increment(ref _linkRevision);
            NoteSwitchStarted();
            Interlocked.Exchange(ref _departureSignal, 1);
        }
    }

    public void MarkLinkClosed()
    {
        lock(_linkGate)
        {
            Interlocked.Increment(ref _linkRevision);
            Interlocked.Exchange(ref _localLink, 0);
        }
        MarkOffline();
    }

    public void ApplyConnectionNotice(DeviceConnectionNotice notice)
    {
        if (notice.DeviceIndex != Index) return;
        if (_wirelessProductId is { } previous && previous != notice.WirelessProductId) InvalidatePairing();
        _wirelessProductId = notice.WirelessProductId;
        if (notice.LinkEstablishedAssumed) ConfirmLink();
        else MarkLinkClosed();
    }

    public void InvalidatePairing()
    {
        MarkLinkClosed();
        _metadataRead = false;
        _restoredRoute=null;Volatile.Write(ref _restorePending,0);
        _wirelessProductId = null;
        lock(_linkGate)
        {
            Interlocked.Exchange(ref _lastSwitchAt, 0);
            Interlocked.Exchange(ref _departureSignal, 0);
        }
        Update(Info with { FeatureIndex = 0, HostCount = 0, CurrentHost = null });
        PairingInvalidated?.Invoke(this);
        _log.Write($"[{Name}] 수신기 등록 변경: 이전 feature·채널 경로를 폐기함");
    }

    private long? ConfirmLink(long? queryRevision=null)
    {
        long revision;
        lock(_linkGate)
        {
            if(queryRevision is {} expected && expected!=HostQueryRevision)return null;
            if (LocalLinkConnected != false && DepartureSignalActive) return null;
            var previous = Interlocked.Exchange(ref _localLink, 1);
            if (previous == 1) return HostQueryRevision;
            revision=Interlocked.Increment(ref _linkRevision);
            if (previous == 0) Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _lastSwitchAt, 0);
            Interlocked.Exchange(ref _departureSignal, 0);
        }
        // Callbacks may observe a newer departure, so the caller rechecks this revision.
        LinkArrived?.Invoke(this);
        return revision;
    }

    public void MarkOffline()
    {
        var info=Volatile.Read(ref _info);
        if (!info.Online) return;
        Update(info with { Online = false });
        _log.Write($"[{Name}] {(!TransportAvailable || LocalLinkConnected==false?"로컬 연결 종료":"채널 제어 응답 미확인: 기기 선택을 유지하고 자동 조회를 다시 시도함")}");
    }

    private void Update(DeviceInfo info,bool forceChanged=false)
    {
        var previous = Volatile.Read(ref _info);
        Volatile.Write(ref _info, info);
        if (previous != info || forceChanged) Changed?.Invoke(this);
    }

    public async Task RefreshAsync(CancellationToken cancel,bool force=false)
    {
        if(force)Interlocked.Exchange(ref _refreshRequested,1);
        if (!await _refreshGate.WaitAsync(0, cancel).ConfigureAwait(false)) return;
        try
        {
            do
            {
                var requested=Interlocked.Exchange(ref _refreshRequested,0)==1;
                if(!requested && !_poll.IsDue(_now()))break;
                var result=await RefreshCoreAsync(cancel).ConfigureAwait(false);
                _poll.Complete(_now(),result,Volatile.Read(ref _expected)!=0);
                RefreshCompleted?.Invoke(this);
            }while(Volatile.Read(ref _refreshRequested)!=0 && !cancel.IsCancellationRequested && _connection.IsOpen);
        }
        finally { _refreshGate.Release(); }
    }

    private async Task<DiscoveryResult> RefreshCoreAsync(CancellationToken cancel)
    {
        var controlConfirmed=false;
        try
        {
            var absent=false;
            var lookupRevision=HostQueryRevision;
            var lookup = await RequestAsync(RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost, Index, _connection.SoftwareId), cancel,
                error=>absent=_connection.IsReceiver && Info.FeatureIndex==0 && HostCount==0 && error.Protocol==1 && error.ErrorCode==0x09).ConfigureAwait(false);
            if (lookup is null)
            {
                MarkOffline();
                return absent?DiscoveryResult.Absent:DiscoveryResult.Retry;
            }
            if(lookupRevision!=HostQueryRevision)
            {_log.Write($"[{Name}] 기능 조회 반영 생략: 조회 중 연결 세대가 바뀜. 새 통로에서 재확인함");return DiscoveryResult.Retry;}
            var feature = RootFeature.ParseGetFeatureResponse(lookup).FeatureIndex;
            if (feature == 0)
            {
                _log.Write($"[{Name}] 채널 전환 미지원: 0x1814 index=0. 전환 명령을 보내지 않음");
                Update(Info with { FeatureIndex = 0, HostCount = 0, Online = false });
                return DiscoveryResult.Unsupported;
            }
            if (Info.FeatureIndex != feature) Update(Info with { FeatureIndex = feature });
            // Start decoding departure events before waiting for host/name queries.
            FeatureReady?.Invoke(this);
            controlConfirmed=await ReadHostAsync(cancel).ConfigureAwait(false) is not null;
            if (!_metadataRead && (Info.Online || Restoring && controlConfirmed)) await ReadMetadataAsync(cancel).ConfigureAwait(false);
            if(controlConfirmed && _metadataRead && Restoring && LocalReplyIsCurrent(Interlocked.Read(ref _onlineLinkRevision)))
            {
                Volatile.Write(ref _restorePending,0);
                Update(Volatile.Read(ref _info),true);
                FeatureReady?.Invoke(this);
                _log.Write($"[{Name}] 캐시 기반 제어 복원 완료: 현재 기기 정보·기능 위치·채널 응답 확인");
            }
            return Info.Online?DiscoveryResult.Ready:DiscoveryResult.Retry;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested || !_connection.IsOpen) { }
        catch (HidppBusyException error)
        {
            if(!controlConfirmed)MarkOffline();
            _log.Write($"[{Name}] 조회 대기: {error.Message}. {(controlConfirmed?"채널 제어 응답은 확인했고 기기 정보 조회를 다시 시도함":"채널 제어 응답 미확인으로 준비 표시를 해제하고 자동 조회를 다시 시도함")}");
        }
        catch (Exception error)
        {
            _log.Write($"[{Name}] 기기 조회 실패: {error.GetType().Name} {error.Message}");
            MarkOffline();
        }
        return DiscoveryResult.Retry;
    }

    public async Task<HostInfo?> ReadHostAsync(CancellationToken cancel)
    {
        if (Info.FeatureIndex == 0) return null;
        var revision=HostQueryRevision;
        var reply = await RequestAsync(ChangeHostFeature.BuildGetHostInfo(Info.FeatureIndex, Index, _connection.SoftwareId), cancel).ConfigureAwait(false);
        if (reply is null)
        {
            MarkOffline();
            return null;
        }
        var host = ChangeHostFeature.ParseGetHostInfo(reply);
        return ReportLocalHost(host,revision) ? host : null;
    }

    internal long HostQueryRevision=>Interlocked.Read(ref _linkRevision);
    private bool LocalReplyIsCurrent(long revision)=>TransportAvailable && LocalLinkConnected==true && revision==HostQueryRevision && !DepartureSignalActive;

    internal bool ReportLocalHost(HostInfo host,long? queryRevision=null)
    {
        // A fresh successful query is evidence of a local link even if the initial 0x41
        // notice said offline. An older reply must never override a later link/departure notice.
        var expected=queryRevision??HostQueryRevision;
        if (!TransportAvailable || (queryRevision is null && LocalLinkConnected==false) || expected!=HostQueryRevision || DepartureSignalActive)
        {
            _log.Write($"[{Name}] 연결 상태 변경 또는 출발 신호 뒤의 늦은 호스트 응답은 새 도착으로 처리하지 않음");
            return false;
        }
        var confirmed=ConfirmLink(expected);
        if(confirmed is not {} revision || !LocalReplyIsCurrent(revision))
        {
            _log.Write($"[{Name}] 호스트 응답 반영 생략: 도착 처리 중 통로 또는 연결 상태가 바뀜");return false;
        }
        var previous = Info;
        Interlocked.Exchange(ref _onlineLinkRevision,revision);
        Update(previous with { Online = true, HostCount = host.HostCount, CurrentHost = host.CurrentHost },!previous.Online);
        if(!LocalReplyIsCurrent(revision))
        {
            _log.Write($"[{Name}] 호스트 응답 반영 생략: 최신 연결 상태와 조회 세대가 달라짐");return false;
        }
        if (!previous.Online || previous.CurrentHost != host.CurrentHost)
            _log.Write($"[{Name}] 이 PC에 연결됨: 채널 {host.CurrentHost + 1}, 0x1814 index=0x{Info.FeatureIndex:X2}");
        FeatureReady?.Invoke(this);
        return LocalReplyIsCurrent(revision);
    }

    public async Task SetHostAsync(int target, CancellationToken cancel)
    {
        if(Restoring)throw new IOException("기기 캐시의 실제 제어 연결을 확인하는 중입니다.");
        if (Info.FeatureIndex == 0) throw new InvalidOperationException("0x1814 조회 전에는 전환할 수 없음");
        if (target < 0 || target >= Info.HostCount) throw new ArgumentOutOfRangeException(nameof(target));
        if (LocalLinkConnected == false) throw new IOException("기기가 이미 이 PC를 떠났습니다.");
        NoteSwitchStarted();
        try { await _connection.SendAsync(ChangeHostFeature.BuildSetCurrentHost(Info.FeatureIndex, target, Index, _connection.SoftwareId).ToBytes(), cancel).ConfigureAwait(false); }
        catch { Interlocked.Exchange(ref _lastSwitchAt, 0); throw; }
    }

    private async Task<HidppFrame?> RequestAsync(HidppFrame request, CancellationToken cancel,Action<HidppErrorInfo>? onError=null)
    {
        var raw=await HidppRequestRetry.ReadAsync(()=>_connection.RequestAsync(request.ToBytes(),cancel),(response,error)=>
        {
            onError?.Invoke(error);
            var busy=error.Protocol==2?error.ErrorCode==0x08:error.ErrorCode==0x07;
            // Unknown-device errors on a root lookup belong to receiver slot discovery, not
            // to a setCurrentHost failure. Preserve the complete frames and distinguish the cause.
            var discovery=_connection.IsReceiver && Info.FeatureIndex==0 && request.FeatureIndex==0 && error.Protocol==1 && error.ErrorCode is 0x08 or 0x09;
            _log.Write($"[{Name}] {(discovery?"수신기 미확인 슬롯 조회":"HID++ 오류")}: {Hex.Format(response)}, 요청 {request} · P{error.Protocol} C{error.ErrorCode:X2}{(busy?" BUSY":"")}");
        },cancel).ConfigureAwait(false);
        return raw is null || HidppFrameParser.IsError(raw)?null:HidppFrameParser.Parse(raw);
    }

    private async Task ReadMetadataAsync(CancellationToken cancel)
    {
        var revision=HostQueryRevision;
        var lookup = await RequestAsync(RootFeature.BuildGetFeature(HidppConstants.FeatureDeviceName, Index, _connection.SoftwareId), cancel).ConfigureAwait(false);
        if (lookup is null || lookup.Param(0) == 0) return;
        var feature = lookup.Param(0);
        var type = await RequestAsync(HidppFrame.Build(feature, 2, deviceIndex: Index, softwareId: _connection.SoftwareId), cancel).ConfigureAwait(false);
        if(type is null)return;
        var kind = type?.Param(0) switch { 0 => "키보드", 2 => "키보드", 3 => "마우스", 4 => "마우스", _ => "기기" };
        var count = await RequestAsync(DeviceNameFeature.BuildGetCount(feature, Index, _connection.SoftwareId), cancel).ConfigureAwait(false);
        if (count is null) return;
        var length = Math.Min(count.Param(0), 128);
        var bytes = new List<byte>();
        while (bytes.Count < length)
        {
            var part = await RequestAsync(DeviceNameFeature.BuildGetName(feature, bytes.Count, Index, _connection.SoftwareId), cancel).ConfigureAwait(false);
            if (part is null) return;
            bytes.AddRange(part.Params.ToArray().Take(length - bytes.Count));
        }
        var name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0');
        if(revision!=HostQueryRevision || !TransportAvailable || DepartureSignalActive)
        {_log.Write($"[{Name}] 기기 정보 반영 생략: 조회 중 연결 상태가 바뀜");return;}
        if(_restoredRoute is {} restored && kind!=restored.Kind)
        {
            _log.Write($"[{Name}] 캐시 기기와 현재 기기 정보가 달라 이전 슬롯 경로를 무효화함");
            InvalidatePairing();Update(Info with{Name=name,Kind=kind});return;
        }
        if(_restoredRoute is {} renamed && name!=renamed.Name)_log.Write($"[{Name}] 기기 이름 변경 확인: 현재 이름으로 캐시를 갱신하고 선택은 유지함");
        Volatile.Write(ref _metadataRead,true);
        Update(Volatile.Read(ref _info) with { Name = string.IsNullOrWhiteSpace(name) ? Name : name, Kind = kind });
        _log.Write($"[{Name}] 기기 종류: {kind}");
    }
}
