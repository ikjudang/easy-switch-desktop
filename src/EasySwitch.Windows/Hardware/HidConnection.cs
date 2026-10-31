using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Hardware;

internal sealed class HidConnection : IDeviceTransport,IAsyncDisposable
{
    private readonly List<(HidInterface Info, FileStream Stream)> _channels;
    private readonly AppLog _log;
    private readonly bool _traceFrames;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _requestGates = new();
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly ConcurrentDictionary<(int Index, int Protocol, ResponseKind Layout), byte> _errorLayouts = new();
    private readonly ConcurrentDictionary<int, RepeatedLogLine> _lookupTimeouts = new();
    private Task[] _readers = [];
    private int _closed;
    private int _recoveryRequired;
    private readonly TimeSpan _writeTimeout;
    public string Key { get; }
    public string Name { get; }
    public bool IsReceiver { get; }
    // Verified alongside the observed 0B..0F response IDs during the 2026-10-21 captures.
    public int SoftwareId => HidppConstants.WindowsSoftwareId;
    public int ProductId => _channels[0].Info.ProductId;
    public string ConnectionLabel => !IsReceiver ? "Bluetooth" : ProductId switch
    {
        HidppUsages.BoltReceiverProductId => "Logi Bolt",
        HidppUsages.UnifyingReceiverProductId => "Unifying",
        _ => "USB",
    };
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    public bool RecoveryRequired=>Volatile.Read(ref _recoveryRequired)!=0;
    public bool ContainsPath(string path) => _channels.Any(c => string.Equals(c.Info.Path, path, StringComparison.OrdinalIgnoreCase));
    public IEnumerable<string> Paths => _channels.Select(c => c.Info.Path);
    public event Action<HidConnection, byte[]>? Received;
    public event Action<HidConnection>? Closed;

    // Windows changes both colXX and the instance's final collection ordinal between short/long collections.
    public static string GroupKey(HidInterface info) => Regex.Replace(
        Regex.Replace(info.Path, @"&col[0-9a-f]+", "", RegexOptions.IgnoreCase),
        @"&[0-9a-f]{4}(?=#\{)", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    public HidConnection(string key, List<(HidInterface Info, FileStream Stream)> channels, AppLog log,TimeSpan? writeTimeout=null,bool traceFrames=false)
    {
        Key = key;
        _channels = channels;
        _log = log;
        _traceFrames=traceFrames;
        _writeTimeout=writeTimeout??TimeSpan.FromSeconds(1);
        IsReceiver = channels[0].Info.IsReceiverHidpp;
        Name = IsReceiver ? $"{ConnectionLabel} {ProductId:X4}" :
            string.IsNullOrWhiteSpace(channels[0].Info.Product) ? $"Bluetooth {ProductId:X4}" : channels[0].Info.Product;
    }

    public void Start() => _readers = _channels.Select(c => ReadAsync(c.Info, c.Stream)).ToArray();

    public async Task ConfigureReceiverNotificationsAsync(CancellationToken cancel)
    {
        if (!IsReceiver) return;
        try
        {
            var request = ReceiverMessages.BuildGetShortRegister(HidppConstants.RegisterNotifications);
            var flags = await RequestAsync(request, cancel).ConfigureAwait(false);
            // A closed receiver handle is already logged by its input loop and has nothing to configure.
            if (flags is null && !IsOpen) return;
            if (flags is null || HidppFrameParser.IsError(flags))
            {
                _log.Write($"[{Name}] 연결 알림 설정 조회 실패. 정기 조회로 연결 상태를 확인함");
                return;
            }
            if ((flags[5] & 1) == 0)
            {
                var enabled = await RequestAsync(ReceiverMessages.BuildEnableWirelessNotifications(flags), cancel).ConfigureAwait(false);
                if (enabled is null || HidppFrameParser.IsError(enabled))
                {
                    _log.Write($"[{Name}] 연결 알림 설정 실패. 다른 플래그는 변경하지 않음");
                    return;
                }
                flags = await RequestAsync(request, cancel).ConfigureAwait(false);
            }
            _log.Write(flags is not null && !HidppFrameParser.IsError(flags) && (flags[5] & 1) != 0
                ? $"[{Name}] 무선 기기 연결 알림 켜짐 확인"
                : $"[{Name}] 무선 기기 연결 알림 확인 실패. 정기 조회를 병행함");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested || !IsOpen) { }
        catch (IOException error) { _log.Write($"[{Name}] 연결 알림 준비 실패: {error.Message}"); }
    }

    private async Task ReadAsync(HidInterface info, FileStream stream)
    {
        var buffer = new byte[info.InputLength];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var length = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (length == 0) throw new EndOfStreamException("HID 입력 종료");
                var raw = buffer[..length];
                Pending? pending = null;
                var kind = raw.Length > 1 && _pending.TryGetValue(raw[1], out pending) ? pending.TryComplete(raw) : null;
                if(_traceFrames)_log.Write($"[{Name}] RX{(kind is not null ? " 응답" : "")} {Hex.Format(raw)}");
                try { Received?.Invoke(this, raw); }
                catch (Exception error) when (!_stop.IsCancellationRequested)
                {
                    _log.Write($"[{Name}] 이벤트 처리 실패. 입력 감시는 계속함: {error.GetType().Name} {error.Message}");
                }
                if (kind is ResponseKind.ErrorSolaarLayout or ResponseKind.ErrorSwappedLayout) NoteErrorLayout(pending!.Request, raw, kind.Value);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Close first: closing can hand a parked departure to the follow path, whose write must not wait for this line.
            MarkClosed();
            _log.Write($"[{Name}] 입력 통로 종료: {error.Message}");
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        foreach (var pending in _pending.Values) pending.Completion.TrySetResult(null);
        Closed?.Invoke(this);
    }

    public async Task<byte[]?> RequestAsync(byte[] request, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
        var gate = _requestGates.GetOrAdd(request[1], _ => new SemaphoreSlim(1));
        // MarkClosed answers waiting requests with null. A cancellation that came only from the
        // closed channel means the same thing; it is not a caller cancellation or a failure.
        try { await gate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ClosedFor(cancel)) { return null; }
        var pending = new Pending(request);
        try
        {
            _pending[request[1]] = pending;
            // SendAsync links the channel's own stop token; pass the caller's token so it can tell
            // a closed channel from a caller cancellation.
            await SendAsync(request, cancel).ConfigureAwait(false);
            byte[]? reply;
            try { reply = await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(750), linked.Token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                var line = $"[{Name} #{request[1]}] 조회 시간 초과: feature 0x{request[2]:X2}, 함수 0x{request[3] >> 4:X1}. 응답을 받지 못함";
                // Polling lookups of an absent receiver slot repeat the same timeout; collapse only those.
                if (IsRootLookup(request)) foreach (var text in _lookupTimeouts.GetOrAdd(request[1], _ => new()).Next(line)) _log.Write(text);
                else _log.Write(line);
                return null;
            }
            if (reply is not null && IsRootLookup(request) && _lookupTimeouts.TryGetValue(request[1], out var repeated) && repeated.Reset("응답 받음") is { } summary)
                _log.Write(summary);
            return reply;
        }
        catch (OperationCanceledException) when (ClosedFor(cancel)) { return null; }
        finally
        {
            _pending.TryRemove(request[1], out _);
            gate.Release();
        }
    }

    private bool ClosedFor(CancellationToken cancel) => _stop.IsCancellationRequested && !cancel.IsCancellationRequested;
    private static bool IsRootLookup(byte[] request) => request.Length >= HidppConstants.HeaderLength && request[2] == HidppConstants.RootFeatureIndex;

    private void NoteErrorLayout(byte[] request, byte[] raw, ResponseKind layout)
    {
        if (HidppMatch.ErrorLayoutAmbiguous(request) || HidppFrameParser.ParseError(raw) is not { } error) return;
        if (!_errorLayouts.TryAdd((raw[1], error.Protocol, layout), 0)) return;
        _log.Write($"[{Name} #{raw[1]}] HID++ {error.Protocol}.0 오류 바이트 배치 확인: {ErrorLayoutLabel(layout)}. 요청 feature 0x{request[2]:X2}, 함수 0x{request[3] >> 4:X1}, 오류 C{error.ErrorCode:X2}. 같은 기기·배치는 다시 기록하지 않음");
    }

    internal static string ErrorLayoutLabel(ResponseKind layout) => layout == ResponseKind.ErrorSolaarLayout
        ? "Solaar 배치(byte3=요청 feature, byte4=요청 func|sw)"
        : "뒤집힌 배치(byte3=요청 func|sw, byte4=요청 feature)";

    public async Task SendAsync(byte[] request, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
        linked.CancelAfter(_writeTimeout);
        var acquired=false;var writing=false;
        try
        {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);acquired=true;
            linked.Token.ThrowIfCancellationRequested();
            linked.CancelAfter(_writeTimeout);
            var shortChannel = _channels.FirstOrDefault(c => c.Info.OutputLength == HidppConstants.ShortLength);
            var channel = request.Length == HidppConstants.ShortLength && shortChannel.Stream is not null
                ? shortChannel : _channels.First(c => c.Info.OutputLength >= HidppConstants.LongLength);
            var payload = new byte[channel.Info.OutputLength];
            request.CopyTo(payload, 0);
            if (request.Length == HidppConstants.ShortLength && payload.Length >= HidppConstants.LongLength)
                payload[0] = HidppConstants.ReportIdLong;
            writing=true;await channel.Stream.WriteAsync(payload, linked.Token).ConfigureAwait(false);
            if(_traceFrames)_log.Write($"[{Name}] TX {Hex.Format(payload)}");
        }
        catch(IOException error) when(error.HResult==unchecked((int)0x8007048F)) // ERROR_DEVICE_NOT_CONNECTED
        {
            // The reader's disconnect callback can arrive after switch verification has ended.
            MarkClosed();
            _log.Write($"[{Name}] 출력 통로 종료: 장치 연결 해제 (HRESULT 0x{error.HResult:X8})");
            throw;
        }
        catch(OperationCanceledException error) when(!cancel.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            if(writing)
            {
                Volatile.Write(ref _recoveryRequired,1);
                _log.Write($"[{Name}] HID 출력 시간 초과: 전송 여부 미확인. 현재 통로를 폐기하고 새 통로의 로컬 응답을 다시 확인함");
                MarkClosed();
            }
            throw new IOException(writing?"HID 출력 시간 초과. 새 통로의 로컬 연결을 확인한 뒤 복구함":"HID 출력 통로 대기 시간 초과. 전송은 시작하지 않음",error);
        }
        catch(OperationCanceledException error) when(!cancel.IsCancellationRequested && _stop.IsCancellationRequested)
        {
            // The input loop closed this channel (device removed or handle failed). Report it as an
            // I/O condition so callers classify it by the transport state, not as an unexpected failure.
            throw new HidChannelClosedException(writing?"HID 통로가 쓰기 중 닫힘. 전송 여부 미확인":"HID 통로가 닫혀 전송을 시작하지 않음",error);
        }
        finally { if(acquired)_writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        MarkClosed();
        foreach (var channel in _channels) await channel.Stream.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_readers).ConfigureAwait(false);
    }

    private sealed class Pending(byte[] request)
    {
        public byte[] Request => request;
        public TaskCompletionSource<byte[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ResponseKind? TryComplete(byte[] raw) => HidppMatch.Classify(request, raw) is { } kind && Completion.TrySetResult(raw) ? kind : null;
    }
}

internal sealed class HidChannelClosedException(string message, Exception inner) : IOException(message, inner);
