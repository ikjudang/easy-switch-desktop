using System.Collections.Concurrent;
using System.Diagnostics;
using EasySwitch.Hidpp;

namespace EasySwitch.Probe;

internal sealed record ProbeOptions(string LogDirectory, double Minutes, int SoftwareId, int TimeoutMs, bool ErrorProbe, bool TraceFrames)
{
    public static ProbeOptions Parse(string[] args)
    {
        // Deliberately not the app's id (0x09): the installed service usually runs while the probe measures,
        // both open shared handles and see each other's replies, and the same id would let each complete
        // the other's pending request. A different id also lets the probe report the app as another sender.
        var options = new ProbeOptions(Path.Combine(Environment.CurrentDirectory, "logs"), 0, HidppConstants.DefaultSoftwareId, 1500, false, false);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--trace-frames") { options = options with { TraceFrames = true }; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"옵션 값이 없음: {name}");
            var value = args[++i];
            options = name switch
            {
                "--log-dir" => options with { LogDirectory = value },
                "--minutes" => options with { Minutes = double.Parse(value) },
                "--swid" => options with { SoftwareId = Convert.ToInt32(value, 16) },
                "--timeout-ms" => options with { TimeoutMs = int.Parse(value) },
                "--error-probe" => options with { ErrorProbe = bool.Parse(value) },
                _ => throw new ArgumentException($"알 수 없는 옵션: {name}"),
            };
        }
        return options;
    }
}

/// <summary>
/// PC 앱을 짜기 전에 이 PC에서 가려야 할 것을 한 번에 잰다(AGENTS.md "먼저 실측할 것").
///  - Logi Options+가 켜져 있어도 HID++ 통로가 열리는가, 전환 신호가 우리에게도 오는가, Options+의 software id는 무엇인가
///  - 볼트 수신기 뒤 기기 번호와 도착 알림(0x41), 블루투스 직결 기기의 통로가 생기고 사라지는 시각
///  - HID++ 에러 응답의 바이트 배치(안드로이드 파서와 Solaar 중 어느 쪽이 맞는가)
///
/// <b>기기의 채널을 바꾸는 명령(setCurrentHost)은 보내지 않는다.</b> 보내는 것은 읽기 조회(feature 위치, 현재 채널, 이름,
/// 수신기 알림 설정)이다. 에러 배치 실험은 --error-probe true로 명시한 경우에만 실행한다.
/// </summary>
internal sealed class Probe(ProbeLog log, ProbeOptions options)
{
    private const int InvalidFeatureIndex = 0x7F;

    private readonly ConcurrentDictionary<string, HidppLink> _links = new();
    private readonly Dictionary<string, HidInterface> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _probing = new();
    private readonly ConcurrentDictionary<int, int> _otherSoftwareIds = new();
    private readonly ConcurrentBag<string> _errorLayouts = [];
    private readonly ConcurrentBag<string> _openFailures = [];
    private int _frames;
    private int _switchSignals;
    private CancellationToken _cancel;

    public async Task RunAsync(CancellationToken cancel)
    {
        _cancel = cancel;
        log.Write("EasySwitch 측정 도구 — 기기의 채널을 바꾸는 명령은 보내지 않습니다. 읽기 조회와 듣기만 합니다.");
        log.Write($"로그 파일: {log.FilePath}");
        log.Write($"윈도우 {Environment.OSVersion.Version}, 우리 software id 0x{options.SoftwareId:X}, 응답 상한 {options.TimeoutMs}ms");
        log.Write($"HID++ 프레임 원문 기록: {(options.TraceFrames ? "켜짐" : "꺼짐")}");
        log.Write($"에러 배치 실험: {(options.ErrorProbe ? "실행" : "실행하지 않음")}. 정기 기기 조회 없이 시작 및 연결 알림 때 조회합니다.");
        var logi = Process.GetProcesses().Where(p => p.ProcessName.Contains("logi", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ProcessName).Distinct().Order().ToList();
        log.Write(logi.Count == 0 ? "로지텍 프로세스: 없음" : $"로지텍 프로세스: {string.Join(", ", logi)}");

        var paths = Win32Hid.ListLogitechPaths();
        log.Write($"로지텍 HID collection {paths.Count}개");
        foreach (var path in paths.Order()) Remember(path, "있음");
        await OpenLinksAsync(paths);

        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(500, cancel); } catch (OperationCanceledException) { break; }
            await WatchAsync();
        }

        log.Write("— 요약 —");
        log.Write($"받은 리포트 {_frames}개, 그중 전환 신호 {_switchSignals}개");
        log.Write(_otherSoftwareIds.IsEmpty ? "다른 앱 응답: 못 봄" :
            "다른 앱 응답의 software id: " + string.Join(", ", _otherSoftwareIds.OrderBy(p => p.Key).Select(p => $"0x{p.Key:X}({p.Value}개)")));
        log.Write(_errorLayouts.IsEmpty ? "에러 배치: 판정 못 함" : "에러 배치: " + string.Join(" / ", _errorLayouts));
        if (!_openFailures.IsEmpty) log.Write("열지 못한 통로: " + string.Join(" / ", _openFailures));
        foreach (var link in _links.Values) await link.DisposeAsync();
        log.Write($"끝. 로그 파일: {log.FilePath}");
    }

    private HidInterface? Remember(string path, string state)
    {
        var info = Win32Hid.Describe(path);
        if (info is null)
        {
            log.Write($"통로 {state}: 정체를 읽지 못함");
            return null;
        }
        _known[path] = info;
        log.Write($"통로 {state}: {info.Label} {info.Shape}{(info.IsBleHidpp || info.IsReceiverHidpp ? "  ← HID++" : "")}");
        return info;
    }

    private async Task WatchAsync()
    {
        var now = Win32Hid.ListLogitechPaths();
        var added = now.Where(p => !_known.ContainsKey(p)).ToList();
        var removed = _known.Keys.Where(p => !now.Contains(p)).ToList();
        foreach (var path in removed)
        {
            log.Write($"통로 사라짐: {_known[path].Label} {_known[path].Shape}");
            var key = _known[path].GroupKey;
            _known.Remove(path);
            if (_links.TryRemove(key, out var link))
            {
                log.Write($"[{link.Name}] 통로를 닫습니다(기기가 떠남).");
                await link.DisposeAsync();
            }
        }
        foreach (var path in added) Remember(path, "생김");
        if (added.Count > 0) await OpenLinksAsync(added.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private async Task OpenLinksAsync(HashSet<string> paths)
    {
        var groups = paths.Where(_known.ContainsKey).Select(p => _known[p])
            .Where(i => i.IsBleHidpp || i.IsReceiverHidpp)
            .GroupBy(i => i.GroupKey);
        foreach (var group in groups)
        {
            if (_links.ContainsKey(group.Key)) continue;
            var openedAt = DateTime.Now;
            var channels = new List<(HidInterface, FileStream)>();
            foreach (var info in group)
            {
                var (stream, error) = Win32Hid.OpenReadWrite(info.Path);
                if (stream is null)
                {
                    var reason = error switch { 5 => "접근 거부", 32 => "다른 프로그램이 독점", _ => "실패" };
                    log.Write($"통로 열기 실패: {info.Label} — 윈도우 오류 {error}({reason})");
                    _openFailures.Add($"{info.Label} 오류 {error}");
                    continue;
                }
                channels.Add((info, stream));
            }
            if (channels.Count == 0) continue;
            var first = group.First();
            var isReceiver = first.IsReceiverHidpp;
            var link = new HidppLink(isReceiver ? $"수신기 {first.ProductId:X4}" :
                string.IsNullOrWhiteSpace(first.Product) ? $"Bluetooth {first.ProductId:X4}" : first.Product, isReceiver, channels);
            link.FrameReceived += OnFrame;
            link.Closed += (l, why) => log.Write($"[{l.Name}] {why}");
            _links[group.Key] = link;
            link.Start();
            log.Write($"[{link.Name}] 통로 열림(읽기·쓰기, {channels.Count}개)");
            _ = isReceiver ? ProbeReceiverAsync(link) : ProbeDeviceAsync(link, HidppConstants.DeviceIndexDirect, "통로가 생김", openedAt);
        }
        await Task.CompletedTask;
    }

    private async Task ProbeReceiverAsync(HidppLink link)
    {
        var result = await RequestAsync(link, ReceiverMessages.BuildGetShortRegister(HidppConstants.RegisterNotifications));
        if (result is { IsError: false } r)
        {
            // 유니파잉 기준: r1 bit0 = 무선 알림(기기 연결 알림 0x41), r1 bit3 = 소프트웨어 있음. 볼트도 같은지는 이 측정으로 본다.
            var wireless = (r.Raw[5] & 0x01) != 0;
            log.Write($"[{link.Name}] 알림 설정 레지스터 = {r.Raw[4]:X2} {r.Raw[5]:X2} {r.Raw[6]:X2} → 무선 알림 {(wireless ? "켜짐" : "꺼짐")}(추정)");
        }
        else
        {
            log.Write($"[{link.Name}] 알림 설정 레지스터를 읽지 못함: {Describe(result)}");
        }
        for (var device = HidppConstants.ReceiverFirstDevice; device <= HidppConstants.ReceiverLastDevice; device++)
            await ProbeDeviceAsync(link, device, "시작 조회", DateTime.Now);
    }

    /// <summary>기기 하나의 0x1814 위치·현재 채널·이름을 읽고, 처음 한 번은 에러 배치를 확인한다.</summary>
    private async Task ProbeDeviceAsync(HidppLink link, int device, string reason, DateTime since)
    {
        var key = $"{link.GroupKey}#{device}";
        if (!_probing.TryAdd(key, 0)) return;
        try
        {
            var tag = Tag(link, device);
            var lookup = await RequestAsync(link, RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost, device, options.SoftwareId).ToBytes());
            if (lookup is null or { IsError: true })
            {
                log.Write($"{tag} 0x1814 위치 조회: {Describe(lookup)}");
                return;
            }
            var index = lookup.Raw[4];
            var ms = (DateTime.Now - since).TotalMilliseconds;
            if (index == 0)
            {
                log.Write($"{tag} 0x1814 미지원(index 0)");
                return;
            }
            Features(link, device)[HidppConstants.FeatureChangeHost] = index;
            log.Write($"{tag} 0x1814 = 0x{index:X2} ({reason} 뒤 {ms:F0}ms에 첫 응답, 왕복 {lookup.Elapsed.TotalMilliseconds:F0}ms)");

            var hostReply = await RequestAsync(link, ChangeHostFeature.BuildGetHostInfo(index, device, options.SoftwareId).ToBytes());
            if (hostReply is { IsError: false } && HidppFrameParser.Parse(hostReply.Raw) is { } hostFrame)
            {
                try
                {
                    var host = ChangeHostFeature.ParseGetHostInfo(hostFrame);
                    log.Write($"{tag} 현재 채널 ch{host.CurrentHost + 1} / 채널 {host.HostCount}개 — 이 PC가 ch{host.CurrentHost + 1}");
                }
                catch (InvalidDataException error)
                {
                    log.Write($"{tag} 현재 채널 응답이 이상함: {error.Message}{RawSuffix(hostReply.Raw)}");
                }
            }
            else
            {
                log.Write($"{tag} 현재 채널 조회: {Describe(hostReply)}");
            }

            await ReadNameAsync(link, device);
            if (options.ErrorProbe) await CheckErrorLayoutAsync(link, device);
        }
        finally
        {
            _probing.TryRemove(key, out _);
        }
    }

    private async Task ReadNameAsync(HidppLink link, int device)
    {
        var lookup = await RequestAsync(link, RootFeature.BuildGetFeature(HidppConstants.FeatureDeviceName, device, options.SoftwareId).ToBytes());
        if (lookup is not { IsError: false } || lookup.Raw[4] == 0) return;
        var index = lookup.Raw[4];
        var countReply = await RequestAsync(link, DeviceNameFeature.BuildGetCount(index, device, options.SoftwareId).ToBytes());
        if (countReply is not { IsError: false } || HidppFrameParser.Parse(countReply.Raw) is not { } countFrame) return;
        var length = DeviceNameFeature.ParseCount(countFrame);
        var name = "";
        while (name.Length < length && name.Length < 64)
        {
            var chunk = await RequestAsync(link, DeviceNameFeature.BuildGetName(index, name.Length, device, options.SoftwareId).ToBytes());
            if (chunk is not { IsError: false } || HidppFrameParser.Parse(chunk.Raw) is not { } chunkFrame) break;
            var piece = DeviceNameFeature.ParseNameChunk(chunkFrame, length - name.Length);
            if (piece.Length == 0) break;
            name += piece;
        }
        if (name.Length == 0) return;
        link.DeviceNames[device] = name;
        log.Write($"{Tag(link, device)} 이름: {name}");
    }

    /// <summary>
    /// 없는 feature 번호(0x7F)로 요청해 에러 응답의 바이트 순서를 본다. feature(0x7F)와 func|sw(0x0A)가 달라서
    /// 어느 쪽이 먼저 오는지로 Solaar 배치와 안드로이드 파서의 배치가 갈린다. 기기마다 한 번만 한다.
    /// </summary>
    private async Task CheckErrorLayoutAsync(HidppLink link, int device)
    {
        var tag = Tag(link, device);
        if (_errorLayouts.Any(e => e.StartsWith(tag, StringComparison.Ordinal))) return;
        var request = HidppFrame.Build(InvalidFeatureIndex, functionId: 0, deviceIndex: device, softwareId: options.SoftwareId).ToBytes();
        var reply = await RequestAsync(link, request);
        var verdict = reply?.Kind switch
        {
            ResponseKind.ErrorSolaarLayout => $"Solaar 배치([마커][feature][func|sw][코드]), 코드 0x{reply.ErrorCode:X2}",
            ResponseKind.ErrorSwappedLayout => $"안드로이드 파서 배치([마커][func|sw][feature][코드]), 코드 0x{reply.ErrorCode:X2}",
            ResponseKind.Response => "에러가 아니라 정상 응답이 옴(예상 밖)",
            _ => "응답 없음(시간 초과)",
        };
        _errorLayouts.Add($"{tag} {verdict}");
        log.Write($"{tag} 에러 배치 확인: {verdict}{(reply is null ? "" : RawSuffix(reply.Raw))}");
    }

    private async Task<RequestResult?> RequestAsync(HidppLink link, byte[] request)
    {
        try
        {
            if (options.TraceFrames) log.Write($"[{link.Name}] TX 시도 {Hex.Format(request)}");
            return await link.RequestAsync(request, TimeSpan.FromMilliseconds(options.TimeoutMs), _cancel);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            log.Write($"[{link.Name}] 보내기 실패: {error.GetType().Name} {error.Message}{RawSuffix(request)}");
            return null;
        }
    }

    private void OnFrame(HidppLink link, byte[] raw, bool mine)
    {
        Interlocked.Increment(ref _frames);
        var description = Explain(link, raw, mine);
        if (description is not null)
        {
            var tag = Tag(link, raw.Length > 1 ? raw[1] : 0);
            log.Write(options.TraceFrames
                ? $"{tag} RX {description} | {Hex.Format(raw)}"
                : $"{tag} {description}");
        }
        if (link.IsReceiver && ReceiverMessages.ParseDeviceConnection(raw) is { LinkEstablishedAssumed: true } notice)
            _ = ProbeDeviceAsync(link, notice.DeviceIndex, "0x41 연결 알림", DateTime.Now);
    }

    /// <summary>Always update summary counters; describe only connectivity or opted-in raw frames.</summary>
    private string? Explain(HidppLink link, byte[] raw, bool mine)
    {
        // Ordinary keyboard and pointer reports use other report IDs and are never logged.
        if (raw.Length == 0 || raw[0] != HidppConstants.ReportIdShort && raw[0] != HidppConstants.ReportIdLong) return null;
        if (raw.Length < HidppConstants.ShortLength) return options.TraceFrames ? $"짧은 HID++ 리포트({raw.Length}B)" : null;
        int device = raw[1], b2 = raw[2], b3 = raw[3];
        if (b2 == HidppConstants.Hidpp20Error)
            return options.TraceFrames || mine ? $"HID++ 2.0 에러 — byte3 0x{b3:X2}, byte4 0x{raw[4]:X2}, 코드 0x{raw[5]:X2}{(mine ? " (우리 요청)" : "")}" : null;
        if (b2 == HidppConstants.Hidpp10Error)
            return options.TraceFrames || mine ? $"HID++ 1.0 에러 — byte3 0x{b3:X2}, byte4 0x{raw[4]:X2}, 코드 0x{raw[5]:X2}{(mine ? " (우리 요청)" : "")}" : null;
        if (link.IsReceiver && b2 is >= 0x40 and <= 0x4F)
        {
            if (ReceiverMessages.ParseDeviceConnection(raw) is { } notice)
                return $"수신기 연결 알림(0x41): 프로토콜 0x{notice.Protocol:X2}, 플래그 0x{notice.Flags:X2} → 링크 {(notice.LinkEstablishedAssumed ? "있음" : "없음")}(추정), 무선 PID {notice.WirelessProductId:X4}";
            if (b2 == HidppConstants.Hidpp10DeviceDisconnection) return "수신기 등록 해제 알림(0x40)";
            return options.TraceFrames ? $"수신기 알림 0x{b2:X2}" : null;
        }
        if (link.IsReceiver && device == HidppConstants.DeviceIndexDirect && b2 is >= 0x80 and <= 0x83)
            return options.TraceFrames ? $"수신기 레지스터 응답(sub_id 0x{b2:X2}, 주소 0x{b3:X2}) — {(mine ? "우리 요청" : "다른 앱의 요청")}" : null;

        var softwareId = b3 & 0x0F;
        var function = b3 >> 4;
        if (softwareId == 0)
        {
            if (Features(link, device).TryGetValue(HidppConstants.FeatureChangeHost, out var index) &&
                HidppFrameParser.Parse(raw) is { } frame &&
                ChangeHostFeature.ParseHostChangeEvent(frame, index) is { } target)
            {
                Interlocked.Increment(ref _switchSignals);
                return $"전환 신호: 목적지 ch{target + 1} (params[0]=0x{raw[4]:X2})";
            }
            return options.TraceFrames ? $"기기 이벤트 — feature 0x{b2:X2} fn {function}" : null;
        }
        if (softwareId == options.SoftwareId)
        {
            if (!options.TraceFrames) return null;
            return mine ? $"우리 응답 — feature 0x{b2:X2} fn {function}"
                : $"우리 software id인데 짝이 없는 응답 — 다른 앱과 겹칠 수 있음 (feature 0x{b2:X2} fn {function})";
        }
        _otherSoftwareIds.AddOrUpdate(softwareId, 1, (_, count) => count + 1);
        return options.TraceFrames ? $"다른 앱 응답 — software id 0x{softwareId:X}, feature 0x{b2:X2} fn {function}" : null;
    }

    private static Dictionary<int, int> Features(HidppLink link, int device)
    {
        lock (link.FeatureIndices)
        {
            if (!link.FeatureIndices.TryGetValue(device, out var map)) link.FeatureIndices[device] = map = [];
            return map;
        }
    }

    private static string Tag(HidppLink link, int device)
    {
        if (!link.IsReceiver) return link.DeviceNames.TryGetValue(device, out var directName) ? $"[{directName}]" : $"[{link.Name}]";
        return link.DeviceNames.TryGetValue(device, out var name) ? $"[{link.Name} #{device} {name}]" : $"[{link.Name} #{device}]";
    }

    private string RawSuffix(byte[] raw) => options.TraceFrames ? $" | {Hex.Format(raw)}" : "";

    private string Describe(RequestResult? result) => result switch
    {
        null => "응답 없음(시간 초과)",
        { IsError: true } => $"에러 코드 0x{result.ErrorCode:X2}{RawSuffix(result.Raw)}",
        _ => $"응답 수신{RawSuffix(result.Raw)}",
    };
}
