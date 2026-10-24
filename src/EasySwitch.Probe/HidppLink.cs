using EasySwitch.Hidpp;

namespace EasySwitch.Probe;

/// <summary>
/// 한 기기(또는 한 수신기)로 가는 HID++ 통로 묶음. 블루투스 직결은 long collection 하나, 수신기는 short·long 두 개다.
/// 요청은 한 번에 하나만 보낸다 — 응답을 요청과 확실히 짝짓기 위해서다.
/// </summary>
internal sealed class HidppLink : IAsyncDisposable
{
    private readonly List<(HidInterface Info, FileStream Stream)> _channels;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private volatile PendingRequest? _pending;

    public HidppLink(string name, bool isReceiver, List<(HidInterface Info, FileStream Stream)> channels)
    {
        Name = name;
        IsReceiver = isReceiver;
        _channels = channels;
    }

    public string Name { get; }
    public bool IsReceiver { get; }
    public string GroupKey => _channels[0].Info.GroupKey;

    /// <summary>기기 번호별로 알아낸 feature index(0x1814 등). 들어온 프레임을 해석하는 데 쓴다.</summary>
    public Dictionary<int, Dictionary<int, int>> FeatureIndices { get; } = [];

    /// <summary>기기 번호별 이름(DeviceName 조회 결과).</summary>
    public Dictionary<int, string> DeviceNames { get; } = [];

    /// <summary>
    /// 들어온 리포트 하나마다 불린다. 마지막 인자는 그 리포트가 우리 요청의 응답이었는지다 — 수신기 레지스터 응답처럼
    /// software id로 주인을 가를 수 없는 경우에 쓴다.
    /// </summary>
    public event Action<HidppLink, byte[], bool>? FrameReceived;

    /// <summary>통로가 끊겼을 때(기기가 떠나 collection이 사라짐 등).</summary>
    public event Action<HidppLink, string>? Closed;

    public void Start()
    {
        foreach (var channel in _channels) _ = ReadLoopAsync(channel.Info, channel.Stream);
    }

    private async Task ReadLoopAsync(HidInterface info, FileStream stream)
    {
        var buffer = new byte[Math.Max(info.InputLength, HidppConstants.LongLength)];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, info.InputLength), _stop.Token);
                if (read <= 0) continue;
                var raw = buffer[..read];
                var pending = _pending;
                var mine = pending is not null && pending.TryComplete(raw);
                FrameReceived?.Invoke(this, raw, mine);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Closed?.Invoke(this, $"{info.Collection} 읽기 끝: {error.GetType().Name} {error.Message}");
        }
    }

    /// <summary>
    /// 요청을 보내고 응답(또는 그 요청에 대한 에러)을 기다린다. 시간 안에 안 오면 null.
    /// short 요청은 short 통로가 있으면 그리로, 없으면 long으로 바꿔 long 통로로 보낸다(블루투스 직결은 long만 받는다).
    /// </summary>
    public async Task<RequestResult?> RequestAsync(byte[] request, TimeSpan timeout, CancellationToken cancel)
    {
        await _requestGate.WaitAsync(cancel);
        try
        {
            var (stream, payload) = ChooseWriter(request);
            var pending = new PendingRequest(request);
            _pending = pending;
            var started = DateTime.Now;
            await stream.WriteAsync(payload, cancel);
            var finished = await Task.WhenAny(pending.Task, Task.Delay(timeout, cancel));
            _pending = null;
            if (finished != pending.Task) return null;
            var (raw, kind) = await pending.Task;
            return new RequestResult(raw, kind, DateTime.Now - started);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private (FileStream Stream, byte[] Payload) ChooseWriter(byte[] request)
    {
        if (request.Length == HidppConstants.ShortLength &&
            _channels.FirstOrDefault(c => c.Info.OutputLength == HidppConstants.ShortLength) is { Stream: not null } shortChannel)
            return (shortChannel.Stream, request);
        var longChannel = _channels.First(c => c.Info.OutputLength == HidppConstants.LongLength);
        if (request.Length == HidppConstants.LongLength) return (longChannel.Stream, request);
        var padded = new byte[HidppConstants.LongLength];
        request.CopyTo(padded, 0);
        padded[0] = HidppConstants.ReportIdLong;
        return (longChannel.Stream, padded);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var channel in _channels) await channel.Stream.DisposeAsync();
    }

    private sealed class PendingRequest(byte[] request)
    {
        private readonly TaskCompletionSource<(byte[], ResponseKind)> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<(byte[], ResponseKind)> Task => _done.Task;

        public bool TryComplete(byte[] raw)
        {
            var kind = HidppMatch.Classify(request, raw);
            return kind is not null && _done.TrySetResult((raw, kind.Value));
        }
    }
}

internal sealed record RequestResult(byte[] Raw, ResponseKind Kind, TimeSpan Elapsed)
{
    public bool IsError => Kind != ResponseKind.Response;
    public int ErrorCode => Raw[5];
}
