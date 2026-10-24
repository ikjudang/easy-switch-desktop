using EasySwitch.Hidpp;

namespace EasySwitch.Core;

public interface IHostDevice
{
    string Id { get; }
    string Name { get; }
    bool TransportAvailable { get; }
    int HostCount { get; }
    int? CurrentHost { get; }
    // A USB receiver stays open when its wireless device leaves. null means the link is not known.
    bool? LocalLinkConnected => null;
    long ConnectionGeneration => 0;
    Task SetHostAsync(int target, CancellationToken cancel);
    Task<HostInfo?> ReadHostAsync(CancellationToken cancel);
}

// Disconnection establishes only that the local link ended. It does not prove arrival at the destination.
public enum SwitchOutcome { AlreadyCurrent, TargetReported, LocalLinkClosed, Unverified, StillOnSource, Cancelled, Failed }
public enum SwitchStep { Started, Joined, WriteStarted, Written, VerifyStarted, TargetReported, SourceReported, NoReply, CommunicationError, Ended }
public enum SwitchEndReason { None, UnsupportedChannel, NoLocalLink, AlreadyCurrent, ConnectionChanged, LocalDeparture, TargetReported, RetryLimit, Superseded, Deadline, Exception }
public sealed record SwitchProgress(SwitchStep Step,int Attempt,int Writes,long ElapsedMs,long DurationMs,int? ReportedHost=null,SwitchOutcome? Outcome=null,SwitchEndReason Reason=SwitchEndReason.None,long QueueMs=0,long WriteMs=0,long VerifyMs=0,int SendCalls=0);

/// <summary>Serializes intent per physical device; a newer target cancels all older retries.</summary>
public sealed class SwitchCoordinator(Action<string> log, TimeSpan? verificationDelay = null, int attempts = 3,
    TimeSpan? retryDelay = null, TimeSpan? budget = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Work> _pending = [];
    private readonly HashSet<Work> _running = [];
    private readonly Dictionary<string, SemaphoreSlim> _deviceGates = [];
    private readonly TimeSpan _delay = verificationDelay ?? TimeSpan.FromMilliseconds(1200);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    private readonly TimeSpan _budget = budget ?? TimeSpan.FromSeconds(9);

    public Task<SwitchOutcome> SwitchAsync(IHostDevice device, int target, string reason, Action<SwitchProgress>? progress=null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        if (target > 2) throw new ArgumentOutOfRangeException(nameof(target));
        Work work;
        lock (_gate)
        {
            var replacesPrevious = false;
            if (_pending.TryGetValue(device.Id, out var previous))
            {
                if (previous.Target == target)
                {
                    if(progress is not null)
                    {
                        previous.Observers.Add(progress);
                        if(previous.LastProgress is {} last)NotifyProgress(progress,last.Step==SwitchStep.Ended?last:last with {Step=SwitchStep.Joined});
                    }
                    return previous.Completion.Task;
                }
                previous.Cancel.Cancel();
                replacesPrevious = true;
            }
            if (!_deviceGates.TryGetValue(device.Id, out var executionGate))
                _deviceGates[device.Id] = executionGate = new SemaphoreSlim(1, 1);
            work = new Work(target, replacesPrevious, executionGate);
            if(progress is not null)work.Observers.Add(progress);
            _pending[device.Id] = work;
            _running.Add(work);
        }
        _ = RunAsync(device, work, reason);
        return work.Completion.Task;
    }

    public void Cancel(string deviceId, string reason)
    {
        lock (_gate)
        {
            if (!_pending.Remove(deviceId, out var work)) return;
            log($"[{deviceId[..Math.Min(8, deviceId.Length)]}] 재시도 취소: {reason}");
            work.Cancel.Cancel();
        }
    }

    public void CancelAll(string reason)
    {
        lock (_gate)
        {
            foreach (var id in _pending.Keys.ToArray()) Cancel(id, reason);
        }
    }

    public async Task StopAsync(string reason)
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = _running.Select(w => w.Completion.Task).ToArray();
            CancelAll(reason);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void NotifyProgress(Action<SwitchProgress> observer,SwitchProgress value)
    {
        try{observer(value);}catch(Exception error){try{log($"코드 로그 기록 실패: {error.GetType().Name}");}catch{}}
    }
    private async Task RunAsync(IHostDevice device, Work work, string reason)
    {
        var result = SwitchOutcome.Failed;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(work.Cancel.Token);
        deadline.CancelAfter(_budget);
        var cancel = deadline.Token;
        var acquired = false;
        var writes = 0;
        var attemptNumber=0;
        var sendCalls=0;
        var endReason=SwitchEndReason.None;
        var phaseAt=0L;
        var phase=0;
        long queueMs=0,writeMs=0,verifyMs=0;
        int? lastHost=null;
        long EndPhase()
        {
            if(phase==0)return 0;
            var duration=work.Clock.ElapsedMilliseconds-phaseAt;
            if(phase==1)writeMs+=duration;else verifyMs+=duration;
            phase=0;return duration;
        }
        void Report(SwitchStep step,long duration=0,int? host=null)
        {
            if(host is not null)lastHost=host;
            var value=new SwitchProgress(step,attemptNumber,writes,work.Clock.ElapsedMilliseconds,duration,step==SwitchStep.Ended?lastHost:host,step==SwitchStep.Ended?result:null,endReason,queueMs,writeMs,verifyMs,sendCalls);
            Action<SwitchProgress>[] observers;
            lock(_gate){work.LastProgress=value;observers=work.Observers.ToArray();}
            foreach(var observer in observers)NotifyProgress(observer,value);
        }
        try
        {
            // Cancellation can arrive during a transport write. Finish that write before a newer intent writes.
            await work.ExecutionGate.WaitAsync(cancel).ConfigureAwait(false);
            acquired = true;
            queueMs=work.Clock.ElapsedMilliseconds;
            cancel.ThrowIfCancellationRequested();
            Report(SwitchStep.Started);
            var generation = device.ConnectionGeneration;
            if (device.HostCount < 1 || work.Target >= device.HostCount)
            {
                endReason=SwitchEndReason.UnsupportedChannel;
                log($"[{device.Name}] 전환 생략: 채널 {work.Target + 1} 미지원");
                return;
            }
            if (device.LocalLinkConnected == false || !device.TransportAvailable)
            {
                endReason=SwitchEndReason.NoLocalLink;
                log($"[{device.Name}] 전환 생략: 이 PC의 기기 링크가 없음. 목적지 연결 여부는 미확인");
                result = SwitchOutcome.Unverified;
                return;
            }
            if (device.CurrentHost == work.Target && !work.ReplacesPrevious)
            {
                endReason=SwitchEndReason.AlreadyCurrent;
                log($"[{device.Name}] 전환 생략: 이미 채널 {work.Target + 1}");
                result = SwitchOutcome.AlreadyCurrent;
                return;
            }
            var lastReplyWasSource = false;
            for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
            {
                attemptNumber=attempt;
                cancel.ThrowIfCancellationRequested();
                if (device.ConnectionGeneration != generation)
                {
                    endReason=SwitchEndReason.ConnectionChanged;
                    log($"[{device.Name}] 이전 전환 취소: 기기가 이 PC에 다시 도착함");
                    result = SwitchOutcome.Cancelled;
                    return;
                }
                if (!device.TransportAvailable || device.LocalLinkConnected == false)
                {
                    endReason=writes==0?SwitchEndReason.NoLocalLink:SwitchEndReason.LocalDeparture;
                    log($"[{device.Name}] 로컬 통로 닫힘. 목적지 연결 여부는 확인할 수 없음");
                    result = writes == 0 ? SwitchOutcome.Unverified : SwitchOutcome.LocalLinkClosed;
                    return;
                }
                try
                {
                    sendCalls++;Report(SwitchStep.WriteStarted);phase=1;phaseAt=work.Clock.ElapsedMilliseconds;
                    await device.SetHostAsync(work.Target, cancel).ConfigureAwait(false);
                    writes++;
                    Report(SwitchStep.Written,EndPhase());
                    log($"[{device.Name}] 채널 {work.Target + 1} 전환 명령 전송 ({reason}, {attempt}/{attempts})");
                    await WaitForDepartureAsync(device, generation, cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    if (device.ConnectionGeneration != generation)
                    {
                        endReason=SwitchEndReason.ConnectionChanged;
                        result = SwitchOutcome.Cancelled;
                        log($"[{device.Name}] 이전 전환 확인 취소: 새 연결에서 오래된 명령을 반복하지 않음");
                        return;
                    }
                    if (!device.TransportAvailable || device.LocalLinkConnected == false)
                    {
                        endReason=SwitchEndReason.LocalDeparture;
                        result = SwitchOutcome.LocalLinkClosed;
                        log($"[{device.Name}] 전송 뒤 기기 링크 종료. 수신기가 남아 있어도 재전송하지 않음. 목적지 도착은 미확인");
                        return;
                    }
                    Report(SwitchStep.VerifyStarted);phase=2;phaseAt=work.Clock.ElapsedMilliseconds;
                    var host = await device.ReadHostAsync(cancel).ConfigureAwait(false);
                    var queryMs=EndPhase();
                    cancel.ThrowIfCancellationRequested();
                    if (device.ConnectionGeneration != generation)
                    {
                        endReason=SwitchEndReason.ConnectionChanged;
                        result = SwitchOutcome.Cancelled;
                        log($"[{device.Name}] 이전 전환 취소: 확인 중 새 연결이 시작됨");
                        return;
                    }
                    if (!device.TransportAvailable || device.LocalLinkConnected == false)
                    {
                        endReason=SwitchEndReason.LocalDeparture;
                        result = SwitchOutcome.LocalLinkClosed;
                        log($"[{device.Name}] 확인 중 기기 링크 종료. 목적지 도착은 미확인");
                        return;
                    }
                    if (host?.CurrentHost == work.Target)
                    {
                        endReason=SwitchEndReason.TargetReported;
                        Report(SwitchStep.TargetReported,queryMs,host.CurrentHost);
                        result = SwitchOutcome.TargetReported;
                        log($"[{device.Name}] 기기가 채널 {work.Target + 1}을 보고함");
                        return;
                    }
                    lastReplyWasSource = host is not null;
                    Report(host is null?SwitchStep.NoReply:SwitchStep.SourceReported,queryMs,host?.CurrentHost);
                    log(host is null
                        ? $"[{device.Name}] 전환 확인 응답 없음. 목적지 도착을 단정하지 않음"
                        : $"[{device.Name}] 아직 채널 {host.CurrentHost + 1}에 응답함");
                }
                catch (IOException error)
                {
                    Report(SwitchStep.CommunicationError,EndPhase());
                    if (device.ConnectionGeneration != generation)
                    {
                        endReason=SwitchEndReason.ConnectionChanged;
                        result = SwitchOutcome.Cancelled;
                        log($"[{device.Name}] 통신 중 새 연결이 시작돼 이전 전환을 취소함");
                        return;
                    }
                    if (!device.TransportAvailable || device.LocalLinkConnected == false)
                    {
                        endReason=writes>0?SwitchEndReason.LocalDeparture:SwitchEndReason.NoLocalLink;
                        result = writes > 0 ? SwitchOutcome.LocalLinkClosed : SwitchOutcome.Unverified;
                        log($"[{device.Name}] 통신 중 기기 링크 종료. 목적지 도착은 미확인");
                        return;
                    }
                    lastReplyWasSource = false;
                    log($"[{device.Name}] 일시적 전환 통신 오류 ({attempt}/{attempts}): {error.Message}");
                }
                if (attempt < attempts) await Task.Delay(_retryDelay, cancel).ConfigureAwait(false);
            }
            result = lastReplyWasSource ? SwitchOutcome.StillOnSource : SwitchOutcome.Unverified;
            endReason=SwitchEndReason.RetryLimit;
            log($"[{device.Name}] 전환 확인 종료: {result}, 재시도 한도 도달");
        }
        catch (OperationCanceledException) when (work.Cancel.IsCancellationRequested)
        {
            endReason=SwitchEndReason.Superseded;
            result = SwitchOutcome.Cancelled;
            log($"[{device.Name}] 이전 전환 작업 취소");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            endReason=SwitchEndReason.Deadline;
            result = SwitchOutcome.Unverified;
            log($"[{device.Name}] 전환 확인 종료: 전체 시간 상한 {_budget.TotalSeconds:F1}초에 도달함");
        }
        catch (Exception error)
        {
            endReason=SwitchEndReason.Exception;
            log($"[{device.Name}] 전환 실패: {error.GetType().Name} {error.Message}");
        }
        finally
        {
            EndPhase();if(!acquired)queueMs=work.Clock.ElapsedMilliseconds;
            Report(SwitchStep.Ended);
            if (acquired) work.ExecutionGate.Release();
            lock (_gate)
            {
                if (_pending.TryGetValue(device.Id, out var current) && ReferenceEquals(current, work))
                    _pending.Remove(device.Id);
                work.Cancel.Dispose();
                work.Completion.TrySetResult(result);
                _running.Remove(work);
            }
        }
    }

    private async Task WaitForDepartureAsync(IHostDevice device, long generation, CancellationToken cancel)
    {
        if (_delay == Timeout.InfiniteTimeSpan)
        {
            await Task.Delay(_delay, cancel).ConfigureAwait(false);
            return;
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < _delay)
        {
            if (!device.TransportAvailable || device.LocalLinkConnected == false || device.ConnectionGeneration != generation) return;
            var remaining = _delay - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), cancel).ConfigureAwait(false);
        }
    }

    private sealed class Work(int target, bool replacesPrevious, SemaphoreSlim executionGate)
    {
        public List<Action<SwitchProgress>> Observers { get; }=[];
        public SwitchProgress? LastProgress { get; set; }
        public System.Diagnostics.Stopwatch Clock { get; }=System.Diagnostics.Stopwatch.StartNew();
        public SemaphoreSlim ExecutionGate { get; } = executionGate;
        public int Target { get; } = target;
        public bool ReplacesPrevious { get; } = replacesPrevious;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource<SwitchOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
