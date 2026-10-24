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
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _requestGates = new();
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private Task[] _readers = [];
    private int _closed;
    private int _recoveryRequired;
    private readonly TimeSpan _writeTimeout;
    public string Key { get; }
    public string Name { get; }
    public bool IsReceiver { get; }
    // Verified alongside the observed 0B..0F response IDs during the 2026-10-21 captures.
    public int SoftwareId => 0x09;
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

    public HidConnection(string key, List<(HidInterface Info, FileStream Stream)> channels, AppLog log,TimeSpan? writeTimeout=null)
    {
        Key = key;
        _channels = channels;
        _log = log;
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
                var matched = raw.Length > 1 && _pending.TryGetValue(raw[1], out var pending) && pending.TryComplete(raw);
                _log.Write($"[{Name}] RX{(matched ? " 응답" : "")} {Hex.Format(raw)}");
                try { Received?.Invoke(this, raw); }
                catch (Exception error) when (!_stop.IsCancellationRequested)
                {
                    _log.Write($"[{Name}] 이벤트 처리 실패. 입력 감시는 계속함: {error.GetType().Name} {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _log.Write($"[{Name}] 입력 통로 종료: {error.Message}");
            MarkClosed();
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
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var pending = new Pending(request);
        try
        {
            _pending[request[1]] = pending;
            await SendAsync(request, linked.Token).ConfigureAwait(false);
            try { return await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(750), linked.Token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _log.Write($"[{Name} #{request[1]}] 조회 시간 초과: {Hex.Format(request)}");
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(request[1], out _);
            gate.Release();
        }
    }

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
            _log.Write($"[{Name}] TX {Hex.Format(payload)}");
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
        public TaskCompletionSource<byte[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryComplete(byte[] raw) => HidppMatch.Classify(request, raw) is not null && Completion.TrySetResult(raw);
    }
}
