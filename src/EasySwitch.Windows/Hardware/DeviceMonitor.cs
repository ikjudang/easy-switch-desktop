using System.Collections.Concurrent;
using EasySwitch.Core;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Hardware;

internal sealed class DeviceMonitor : IAsyncDisposable
{
    private readonly AppLog log;
    private readonly DeviceRouteCache? _cache;
    private HashSet<string> _expected=[];
    internal DeviceMonitor(AppLog log,string? cacheDirectory=null)
    {this.log=log;if(cacheDirectory is not null)_cache=new(cacheDirectory,log);}
    internal DeviceRestoreStatus[] Restorations=>_cache?.Status??[];
    internal void ExpectDevices(IEnumerable<string> ids)
    {
        var expected=ids.ToHashSet(StringComparer.Ordinal);
        if(Volatile.Read(ref _expected).SetEquals(expected))return;
        Volatile.Write(ref _expected,expected);_cache?.Expect(expected);
        foreach(var device in _devices.Values)device.ExpectRestore(expected.Contains(device.Id));
        Wake();
    }
    private readonly ConcurrentDictionary<string, HidConnection> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _initialized = new();
    private readonly ConcurrentDictionary<(string Key, int Index), HostDevice> _devices = new();
    private readonly ConcurrentDictionary<string, HidInterface> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, int), EarlySignalGate<byte[]>> _early = new();
    private readonly ConcurrentDictionary<(string, int), byte> _earlyAnnounced = new();
    private readonly ConcurrentDictionary<(string, int), int> _earlyOverflow = new();
    private readonly ConcurrentDictionary<string, byte> _shortReportAnnounced = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Task, byte> _operations = new();
    private readonly Dictionary<string, DateTimeOffset> _openRetry = [];
    private Task? _loop;
    private int _refreshRequested;
    private int _recoveryRequested,_suspended;
    private TaskCompletionSource? _recoveryCompletion;
    private readonly SemaphoreSlim _wake=new(0,1);
    public event Action? Changed;
    public event Action<HostDevice, int>? HostChanged;
    public event Action<HostDevice>? Arrived;
    public event Action<HostDevice>? PairingInvalidated;
    public IReadOnlyList<DeviceInfo> Snapshot() => _devices.Values.Select(d => d.Info).Where(d => d.HostCount > 0).OrderBy(d => d.Name).ToArray();
    public HostDevice? Find(string? id) => id is null ? null : _devices.Values.FirstOrDefault(d => d.Id == id);
    public void Start() => _loop = Task.Run(RunAsync);
    private void Wake(){try{_wake.Release();}catch(SemaphoreFullException){}}
    public void Refresh(){Interlocked.Exchange(ref _refreshRequested,1);Wake();}
    internal bool Started=>_loop is not null;
    public void Suspend(){Volatile.Write(ref _suspended,1);Interlocked.Exchange(ref _recoveryRequested,1);Wake();}
    public void Recover(){Interlocked.Exchange(ref _recoveryRequested,1);Volatile.Write(ref _suspended,0);Wake();}
    public async Task SuspendAndWaitAsync()
    {
        if(_loop is null){Suspend();return;}
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _recoveryCompletion,completion)?.TrySetCanceled();Suspend();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    private void Track(Task task)
    {
        _operations.TryAdd(task, 0);
        _ = task.ContinueWith(t =>
        {
            _operations.TryRemove(t, out _);
            if (t.IsFaulted) log.Write($"기기 작업 실패. 감시 루프는 계속함: {t.Exception!.GetBaseException()}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if(Interlocked.Exchange(ref _recoveryRequested,0)!=0)await ResetConnectionsAsync().ConfigureAwait(false);
                    if(Volatile.Read(ref _suspended)!=0){await _wake.WaitAsync(250,_stop.Token).ConfigureAwait(false);continue;}
                    await ScanAsync().ConfigureAwait(false);
                    var force=Interlocked.Exchange(ref _refreshRequested,0)==1;
                    foreach (var device in _devices.Values.Where(d => d.TransportAvailable && !d.SwitchGraceActive && _initialized.ContainsKey(d.ConnectionKey)))
                    {
                        Track(device.RefreshAsync(_stop.Token,force));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    log.Write($"기기 검색 실패: {error.GetType().Name} {error.Message}");
                }
                await _wake.WaitAsync(100,_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ResetConnectionsAsync()
    {
        var completion=Interlocked.Exchange(ref _recoveryCompletion,null);
        try
        {
            var old=_connections.ToArray();
            foreach(var pair in old)_connections.TryRemove(pair.Key,out _);
            foreach(var device in _devices.Values)device.MarkLinkClosed();
            _devices.Clear();_initialized.Clear();_early.Clear();_earlyAnnounced.Clear();_earlyOverflow.Clear();_shortReportAnnounced.Clear();_descriptions.Clear();_openRetry.Clear();
            foreach(var pair in old)await pair.Value.DisposeAsync().ConfigureAwait(false);
            log.Write("전원 상태 변경: 이전 HID 통로와 보관 신호를 정리하고 기기를 다시 검색함");
            Changed?.Invoke();
            completion?.TrySetResult();
        }
        catch(Exception error){completion?.TrySetException(error);throw;}
    }

    private async Task ScanAsync()
    {
        var paths = HidInventory.Read();
        foreach (var pair in _connections.ToArray())
        {
            if (pair.Value.IsOpen && pair.Value.Paths.All(paths.Contains)) continue;
            if (!_connections.TryRemove(pair.Key, out var connection)) continue;
            _initialized.TryRemove(pair.Key, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var path in _descriptions.Keys.Where(p => !paths.Contains(p))) _descriptions.TryRemove(path, out _);
        foreach (var path in paths.Where(p => !_descriptions.ContainsKey(p)))
        {
            var info = Win32Hid.Describe(path);
            if (info is not null) _descriptions[path] = info;
        }
        var groups = _descriptions.Values.Where(i => (i.IsBleHidpp || i.IsReceiverHidpp) && i.InputLength >= 7 && i.OutputLength >= 7)
            .GroupBy(HidConnection.GroupKey);
        foreach (var group in groups)
        {
            if (_connections.ContainsKey(group.Key)) continue;
            if (_openRetry.TryGetValue(group.Key, out var retryAt) && DateTimeOffset.UtcNow < retryAt) continue;
            _openRetry[group.Key] = DateTimeOffset.UtcNow.AddSeconds(5);
            var channels = new List<(HidInterface, FileStream)>();
            foreach (var info in group)
            {
                var opened = Win32Hid.OpenReadWrite(info.Path);
                if (opened.Stream is null)
                {
                    log.Write($"[{info.Label}] HID 통로 열기 실패: Windows 오류 {opened.Error}");
                    continue;
                }
                channels.Add((info, opened.Stream));
            }
            if (channels.Count == 0) continue;
            var connection = new HidConnection(group.Key, channels, log);
            _connections[group.Key] = connection;
            connection.Received += OnReceived;
            connection.Closed += OnClosed;
            var indices = connection.IsReceiver ? Enumerable.Range(1, 6) : [HidppConstants.DeviceIndexDirect];
            foreach (var index in indices)
            {
                RegisterDevice(connection,index);
            }
            connection.Start();
            log.Write($"[{connection.Name}] 공유 모드로 HID 통로 {channels.Count}개 열림");
            Track(InitializeConnectionAsync(connection));
        }
    }

    internal HostDevice RegisterDevice(IDeviceTransport connection,int index,Func<long>? tickCount=null)
    {
        _devices.TryGetValue((connection.Key,index),out var previous);
        var provisional=new HostDevice(connection,index,log,previous?.Info,tickCount);
        var route=previous is not null && !connection.IsReceiver?null:_cache?.Find(provisional.Id);
        var device=route is null?provisional:new HostDevice(connection,index,log,previous?.Info,tickCount,route);
        device.ExpectRestore(Volatile.Read(ref _expected).Contains(device.Id));
        device.Changed+=OnDeviceChanged;
        device.RefreshCompleted+=device=>OnDeviceState(device,true);
        device.FeatureReady+=ReplayEarlyEvents;
        device.LinkArrived+=arrived=>{if(IsCurrent(arrived))Arrived?.Invoke(arrived);};
        device.PairingInvalidated+=invalidated=>
        {
            if(!IsCurrent(invalidated))return;
            _cache?.Invalidate(invalidated.Id);PairingInvalidated?.Invoke(invalidated);
        };
        _devices[(connection.Key,index)]=device;
        _cache?.Stage(device.Id,DeviceRestoreStage.Checking,route is null?"새 통로에서 제어 연결 확인":"저장 통로 일치. 캐시의 실제 기기·제어 응답 검증");
        return device;
    }
    private bool IsCurrent(HostDevice device)=>_devices.TryGetValue((device.ConnectionKey,device.Index),out var current) && ReferenceEquals(device,current);

    private async Task InitializeConnectionAsync(HidConnection connection)
    {
        await connection.ConfigureReceiverNotificationsAsync(_stop.Token).ConfigureAwait(false);
        if (!connection.IsOpen || !_connections.TryGetValue(connection.Key, out var current) || !ReferenceEquals(current, connection)) return;
        _initialized[connection.Key] = 1;
        await Task.WhenAll(_devices.Where(p => p.Key.Key == connection.Key).Select(p => p.Value.RefreshAsync(_stop.Token,true))).ConfigureAwait(false);
    }

    private void OnClosed(HidConnection connection)
    {
        if(_connections.TryGetValue(connection.Key,out var active) && !ReferenceEquals(active,connection))return;
        foreach (var key in _early.Keys.Where(k => k.Item1 == connection.Key).ToArray())
        {
            _earlyAnnounced.TryRemove(key, out _);
            _earlyOverflow.TryRemove(key, out var overflow);
            if (_early.TryRemove(key, out var queue) && !queue.IsEmpty)
                log.Write($"[{connection.Name}] 보관 이벤트 폐기: feature 조회 전에 통로가 닫혀 새 연결에서 재생하지 않음 (보관 한도 초과 누적 {overflow}개)");
        }
        _shortReportAnnounced.TryRemove(connection.Key, out _);
        foreach (var device in _devices.Values.Where(device=>device.UsesTransport(connection))) device.MarkLinkClosed();
    }

    private void OnDeviceChanged(HostDevice device)
        =>OnDeviceState(device,false);
    private void OnDeviceState(HostDevice device,bool completed)
    {
        if (!_devices.TryGetValue((device.ConnectionKey, device.Index), out var current) || !ReferenceEquals(device, current)) return;
        var info=device.Info;
        var ready=info.Online && device.IdentityReady;
        _cache?.Stage(device.Id,ready?DeviceRestoreStage.Ready:completed?DeviceRestoreStage.Retrying:DeviceRestoreStage.Checking,ready?"현재 통로의 기기·채널 제어 응답 확인":"제어 응답 대기. 선택을 유지하고 자동 재시도");
        if(ready)_cache?.Remember(info,device.WirelessProductId,device.ConnectionKey,device.Index);
        if(!completed)Changed?.Invoke();
    }

    private void OnReceived(HidConnection connection, byte[] raw)
    {
        if(Volatile.Read(ref _suspended)!=0 || Volatile.Read(ref _recoveryRequested)!=0)return;
        if (!_connections.TryGetValue(connection.Key, out var active) || !ReferenceEquals(active, connection)) return;
        if (raw.Length < 7)
        {
            if (_shortReportAnnounced.TryAdd(connection.Key, 0)) log.Write($"[{connection.Name}] 짧은 HID++ 리포트 폐기: {raw.Length}바이트. 같은 통로의 반복은 기록하지 않음");
            return;
        }
        if (connection.IsReceiver && ReceiverMessages.ParseDeviceConnection(raw) is { } notice)
        {
            if (_devices.TryGetValue((connection.Key, notice.DeviceIndex), out var noticed))
            {
                log.Write($"[{noticed.Name}] 0x41 알림: 이 PC 로컬 링크 {(notice.LinkEstablishedAssumed ? "도착" : "이탈")} 추정, 슬롯 {notice.DeviceIndex}, 무선 PID 0x{notice.WirelessProductId:X4}. 현재 채널은 별도 응답으로 확인함");
                noticed.ApplyConnectionNotice(notice);
                if (notice.LinkEstablishedAssumed) Track(noticed.RefreshAsync(_stop.Token,true));
            }
            else log.Write($"[{connection.Name}] 0x41 알림 처리 생략: 슬롯 {notice.DeviceIndex}의 등록 기기 경로가 없음");
            return;
        }
        if (connection.IsReceiver && raw[2] == HidppConstants.Hidpp10DeviceDisconnection)
        {
            if (_devices.TryGetValue((connection.Key, (int)raw[1]), out var unpaired)) unpaired.InvalidatePairing();
            else log.Write($"[{connection.Name}] 등록 해제 알림: 슬롯 {raw[1]}의 등록 기기 경로가 없음");
            var key=(connection.Key,(int)raw[1]);
            _earlyAnnounced.TryRemove(key,out _);
            _earlyOverflow.TryRemove(key,out var overflow);
            if (_early.TryRemove(key, out var removed) && !removed.IsEmpty)
                log.Write($"등록 해제로 보관 중인 이전 기기의 이벤트를 폐기함 (보관 한도 초과 누적 {overflow}개)");
            return;
        }
        if (raw[3] != 0 || HidppFrameParser.IsError(raw)) return;
        if (!_devices.TryGetValue((connection.Key, (int)raw[1]), out var device)) return;
        if (device.Restoring || device.Info.FeatureIndex == 0 || device.CurrentHost is null)
        {
            var key=(connection.Key,(int)raw[1]);
            var queue = _early.GetOrAdd(key, _ => new());
            if (_earlyAnnounced.TryAdd(key, 0))
                log.Write($"[{device.Name}] {(device.Restoring?"기기 복원 검증":"feature 조회")} 중 초기 이벤트 보관 시작");
            if (queue.Hold(raw, device.ConnectionGeneration) && _earlyOverflow.AddOrUpdate(key,1,(_,count)=>count+1)==1)
                log.Write($"[{device.Name}] 초기 이벤트 보관 한도로 가장 오래된 프레임을 폐기함. 추가 폐기 수는 보관 종료 시 기록함");
            return;
        }
        DecodeEvent(device, raw);
    }

    private void ReplayEarlyEvents(HostDevice device)
    {
        if (device.Restoring || device.Info.FeatureIndex == 0 || device.CurrentHost is null) return;
        foreach (var key in _early.Keys.Where(k => k.Item2 == device.Index).ToArray())
        {
            if (!_devices.TryGetValue(key, out var owner) || !ReferenceEquals(owner, device)) continue;
            if (!_early.TryRemove(key, out var queue)) continue;
            _earlyAnnounced.TryRemove(key,out _);
            _earlyOverflow.TryRemove(key,out var overflow);
            if (overflow>1) log.Write($"[{device.Name}] 초기 이벤트 보관 한도 초과로 추가 {overflow-1}개 폐기");
            var held = queue.Open(device.ConnectionGeneration);
            if (held.Discarded > 0) log.Write($"[{device.Name}] 이전 연결 또는 5초 경과 보관 이벤트 {held.Discarded}개 폐기");
            foreach (var frame in held.Frames) DecodeEvent(device, frame);
        }
    }

    private void DecodeEvent(HostDevice device, byte[] raw)
    {
        if (HidppFrameParser.Parse(raw) is not { } frame)
        {
            log.Write($"[{device.Name}] 기기 이벤트 폐기: HID++ 프레임 형식 오류");
            return;
        }
        if (frame.FeatureIndex != device.Info.FeatureIndex) return;
        if (ChangeHostFeature.ParseHostChangeEvent(frame, device.Info.FeatureIndex) is not { } target)
        {
            log.Write($"[{device.Name}] 전환 신호 폐기: 목적지 채널 {frame.Param(1)}이 유효 범위 0~2 밖임");
            return;
        }
        if (target == device.CurrentHost)
        {
            log.Write($"[{device.Name}] 도착 채널 {target + 1} 알림: 출발로 처리하지 않음");
            return;
        }
        device.NoteDepartureSignal();
        log.Write($"[{device.Name}] 전환 신호: 채널 {target + 1}, params[0]=0x{raw[4]:X2}");
        HostChanged?.Invoke(device, target);
        device.MarkOffline();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        foreach (var connection in _connections.Values) await connection.DisposeAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_operations.Keys).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        if(_cache is not null)await _cache.FlushAsync().ConfigureAwait(false);
    }
}
