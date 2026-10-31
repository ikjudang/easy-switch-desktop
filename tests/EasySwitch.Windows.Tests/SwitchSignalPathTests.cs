using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using EasySwitch.Core;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

// Synthetic transports only: departure signals, setCurrentHost replies, channel closure and polling logs.
public sealed class SwitchSignalPathTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EasySwitch.Tests", Guid.NewGuid().ToString("N"));
    private string CacheDirectory => Path.Combine(_root, "engine");
    private readonly long _now = 0; // discovery clock held still; tests force refreshes explicitly

    // ---- H1: a channel closing during verification is a local departure, not a failure ----

    [Fact]
    public async Task VerificationQueryInterruptedByAClosedChannelIsALocalDepartureNotAFailure()
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        await using var connection = new HidConnection("closing", [(Bluetooth(stream.Name), stream)], log);
        var mouse = Ready(connection, log, "Mouse", "마우스");
        connection.Start();
        stream.OnWrite = count => { if (count == 2) stream.Fail(); }; // removed while the verification query waits
        var outcome = await new SwitchCoordinator(_ => { }, verificationDelay: TimeSpan.Zero, retryDelay: TimeSpan.Zero).SwitchAsync(mouse, 1, "fixture");
        Assert.Equal(SwitchOutcome.LocalLinkClosed, outcome);
        Assert.False(connection.IsOpen);
        Assert.Equal(2, stream.Writes);
    }

    [Fact]
    public async Task RequestsAndWritesOnAClosedChannelAreReportedAsClosedNotCancelled()
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        await using var connection = new HidConnection("closed", [(Bluetooth(stream.Name), stream)], log);
        connection.Start(); stream.Fail();
        await Until(() => !connection.IsOpen);
        Assert.Null(await connection.RequestAsync(RootFeature.BuildGetFeature(0x1814, 0xFF, 9).ToBytes(), default));
        var command = ChangeHostFeature.BuildSetCurrentHost(9, 1, 0xFF, 9).ToBytes();
        await Assert.ThrowsAsync<HidChannelClosedException>(() => connection.SendAsync(command, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendAsync(command, cancelled.Token));
        Assert.Equal(0, stream.Writes);
    }

    // ---- H2: a restored keyboard's departure is not parked until its name has been read ----

    [Fact]
    public async Task RestoredReceiverKeyboardInterpretsAParkedDepartureOnceItsDeviceTypeMatches()
    {
        using var log = Log();
        var id = await Seed(new FakeWire("keyboard"), log);
        var wire = new FakeWire("keyboard") { CountHold = Hold() };
        await using var monitor = new DeviceMonitor(log, CacheDirectory); monitor.ExpectDevices([id]);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(wire, 1, () => _now);
        Assert.True(device.Restoring); Assert.False(device.DepartureEventsReady);
        monitor.Process(wire, Event(1, 9, 1));
        Assert.Empty(targets);
        var refresh = device.RefreshAsync(default);
        await wire.CountEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); // type answered, name not yet read
        Assert.Equal(new[] { 1 }, targets.ToArray());
        Assert.True(device.Restoring);
        await Assert.ThrowsAsync<IOException>(() => device.SetHostAsync(2, default)); // commands still wait for restoration
        Assert.Equal(0, wire.Writes);
        wire.CountHold!.SetResult(); await refresh;
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LinkLostNoticeWithTheCachedWirelessIdRecoversADepartureParkedDuringRestoration(bool samePid)
    {
        using var log = Log();
        var seed = new FakeWire("keyboard");
        var id = new HostDevice(seed, 1, log).Id;
        var cache = new DeviceRouteCache(CacheDirectory, log);
        cache.Remember(new(id, "Keyboard", "Logi Bolt", "키보드", true, 3, 0, 9), 0xB366, seed.Key, 1); await cache.FlushAsync();
        var wire = new FakeWire("keyboard") { TypeHold = Hold() };
        await using var monitor = new DeviceMonitor(log, CacheDirectory); monitor.ExpectDevices([id]);
        var targets = Targets(monitor); var invalidated = 0; monitor.PairingInvalidated += _ => invalidated++;
        var device = monitor.RegisterDevice(wire, 1, () => _now);
        var refresh = device.RefreshAsync(default);
        await wire.TypeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); // feature and host known, type unanswered
        Assert.Equal(9, device.Info.FeatureIndex); Assert.Equal(0, device.CurrentHost); Assert.False(device.DepartureEventsReady);
        monitor.Process(wire, Event(1, 9, 1));
        Assert.Empty(targets);
        monitor.Process(wire, Hex.Parse(samePid ? "10 01 41 10 41 66 B3" : "10 01 41 10 41 42 B0"));
        if (samePid) { Assert.Equal(new[] { 1 }, targets.ToArray()); Assert.Equal(0, invalidated); }
        else { Assert.Empty(targets); Assert.Equal(1, invalidated); }
        wire.TypeHold!.SetResult(); await refresh;
        Assert.Equal(0, wire.Writes);
    }

    [Fact]
    public async Task RestoredBluetoothKeyboardInterpretsDeparturesAfterFeatureAndHostWithoutMetadata()
    {
        using var log = Log();
        var id = await Seed(new FakeWire("bt-keyboard", receiver: false), log);
        var wire = new FakeWire("bt-keyboard", receiver: false) { TypeHold = Hold() };
        await using var monitor = new DeviceMonitor(log, CacheDirectory); monitor.ExpectDevices([id]);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(wire, 0xFF, () => _now);
        Assert.True(device.Restoring); Assert.True(device.DepartureEventsReady);
        monitor.Process(wire, Event(0xFF, 9, 2)); // before the feature lookup: parked
        Assert.Empty(targets);
        // The departure is interpreted at the host reply. The device has left, so no metadata query follows;
        // before the fix the event stayed parked behind the (held) type query.
        await device.RefreshAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 2 }, targets.ToArray());
        Assert.False(wire.TypeEntered.Task.IsCompleted);
        wire.TypeHold!.SetResult();
    }

    [Fact]
    public async Task RestoredSlotHoldingAnotherDeviceTypeNeverInterpretsItsEvents()
    {
        using var log = Log();
        var id = await Seed(new FakeWire("keyboard"), log);
        var wire = new FakeWire("keyboard") { Kind = 3, Name = "Mouse" };
        await using var monitor = new DeviceMonitor(log, CacheDirectory); monitor.ExpectDevices([id]);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(wire, 1, () => _now);
        monitor.Process(wire, Event(1, 9, 1));
        await device.RefreshAsync(default);
        Assert.Empty(targets); Assert.Equal(0, device.Info.FeatureIndex); Assert.Equal(0, wire.Writes);
    }

    // ---- H2 (measured 2026-10-24): a departure parked when the link ends is not dropped silently ----

    [Fact]
    public async Task ClosedBluetoothHandleHandsAParkedDepartureToFollowWithTheCachedIndex()
    {
        using var log = Log();
        var id = await Seed(new FakeWire("bt-keyboard", receiver: false), log);
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        await using var connection = new HidConnection("bt-keyboard", [(Bluetooth(stream.Name), stream)], log);
        await using var monitor = new DeviceMonitor(log, CacheDirectory); monitor.ExpectDevices([id]);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(connection, 0xFF);
        Assert.Equal(id, device.Id); Assert.Equal(0, device.Info.FeatureIndex);
        monitor.Process(connection, Event(0xFF, 9, 1)); // RX 11 FF 09 00 02 01 on the freshly opened handle
        Assert.Empty(targets);
        Closed(monitor, connection);
        Assert.Equal(new[] { 1 }, targets.ToArray());
        Assert.Single(Lines(log, "보관한 전환 신호를 출발로 처리함: HID 통로가 닫힘 직전에 받은 신호, 채널 2, 0x1814 위치 0x09(통신 정보 캐시의 위치)"));
    }

    [Theory]
    [InlineData(9, true)]  // lookup answered, host query not: the current index decodes it
    [InlineData(0, false)] // nothing known about this device: logged, not guessed
    public async Task ClosedHandleWithoutAnAnsweredHostQuerySettlesParkedFramesByWhatIsKnown(int feature, bool follows)
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        await using var connection = new HidConnection("bt-fresh", [(Bluetooth(stream.Name), stream)], log);
        await using var monitor = new DeviceMonitor(log);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(connection, 0xFF);
        typeof(HostDevice).GetField("_info", Hidden)!.SetValue(device, device.Info with { FeatureIndex = feature });
        monitor.Process(connection, Event(0xFF, 9, 2));
        Closed(monitor, connection);
        Assert.Equal(follows ? new[] { 2 } : [], targets.ToArray());
        if (!follows) Assert.Single(Lines(log, "보관 이벤트 폐기: HID 통로가 닫힘. 해석하지 않은 이유: 이 기기의 0x1814 위치를 아직 모름"));
    }

    [Theory]
    [InlineData(false, true)] // ordinary slot whose host query was not answered yet
    [InlineData(true, false)] // restored slot with no cached wireless id and an unread type: identity unknown
    public async Task ReceiverLinkLostNoticeSettlesParkedFramesOnlyForAKnownIdentity(bool restored, bool follows)
    {
        using var log = Log();
        var wire = new FakeWire("keyboard");
        string? id = null;
        if (restored) id = await Seed(new FakeWire("keyboard"), log);
        await using var monitor = new DeviceMonitor(log, restored ? CacheDirectory : null);
        if (id is not null) monitor.ExpectDevices([id]);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(wire, 1, () => _now);
        Assert.Equal(restored, device.Restoring);
        if (!restored) typeof(HostDevice).GetField("_info", Hidden)!.SetValue(device, device.Info with { FeatureIndex = 9 });
        monitor.Process(wire, Event(1, 9, 1));
        Assert.Empty(targets);
        monitor.Process(wire, Hex.Parse("10 01 41 10 41 66 B3"));
        Assert.Equal(follows ? new[] { 1 } : [], targets.ToArray());
        if (!follows) Assert.Single(Lines(log, "해석하지 않은 이유: 캐시 복원 기기의 신원 확인 전"));
    }

    // Target 1 is a departure: the mouse follows. Target 0 is this PC's own channel, i.e. an arrival notice
    // misread as a departure at link end: the follow ends AlreadyCurrent with no write (assumes H14's same layout).
    [Theory]
    [InlineData(1)] [InlineData(0)]
    public Task KeyboardDepartureParkedOnAClosingHandleStillMovesTheMouse(int target) => Sta(async () =>
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        await using var connection = new HidConnection("bt-keyboard", [(Bluetooth(stream.Name), stream)], log);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        var keyboardId = new HostDevice(connection, 0xFF, log).Id; var mouseId = new HostDevice(mouseWire, 0xFF, log).Id;
        var store = new SettingsStore(Path.Combine(_root, "settings"));
        store.Save(new() { FollowEnabled = true, SelectedDeviceIds = [keyboardId, mouseId], SelectedDeviceNames = [new(keyboardId, "Keyboard", "키보드"), new(mouseId, "Mouse", "마우스")] });
        await using var vm = new MainViewModel(store, log, Dispatcher.CurrentDispatcher);
        var monitor = (DeviceMonitor)typeof(MainViewModel).GetField("_monitor", Hidden)!.GetValue(vm)!;
        var keyboard = monitor.RegisterDevice(connection, 0xFF);
        typeof(HostDevice).GetField("_info", Hidden)!.SetValue(keyboard, keyboard.Info with { Name = "Keyboard", Kind = "키보드", FeatureIndex = 9 });
        var mouse = monitor.RegisterDevice(mouseWire, 0xFF, () => _now);
        await mouse.RefreshAsync(default); Assert.True(mouse.Info.Online);
        monitor.Process(connection, Event(0xFF, 9, target)); // host query not answered yet: parked
        Assert.Equal(0, mouseWire.Writes);
        Closed(monitor, connection);                          // the keyboard left this PC 0.2 s later
        if (target == 1)
        {
            await Until(() => mouseWire.Writes == 1);
            Assert.Equal(1, mouseWire.LastWrite![4]);
            return;
        }
        await Until(() => log.RecentCodes().Any(code => code.Code.Contains("|F1|M1|END|S|AC|")));
        Assert.Equal(0, mouseWire.Writes);
    });

    // ---- advisor finding: a cross-paired DeviceName lookup must not become setCurrentHost(channel 1) ----

    [Fact]
    public async Task DeviceNameLookupAnsweredWithTheChangeHostIndexNeverReadsWithIt()
    {
        using var log = Log();
        var wire = new FakeWire("keyboard") { NameLookupReply = 9 };
        var device = new HostDevice(wire, 1, log, tickCount: () => _now);
        await device.RefreshAsync(default);
        Assert.DoesNotContain(wire.Requests, request => request.Feature == 9 && request.Function != 0);
        Assert.Single(Lines(log, "기기 이름 기능 위치 응답이 0x1814 위치 0x09와 같음"));
        Assert.False(device.IdentityReady);
    }

    // ---- H3: replies and errors to our setCurrentHost are visible ----

    [Fact]
    public async Task ReplyOrErrorToOurSetCurrentHostIsLoggedWithElapsedTimeAndOthersAreNot()
    {
        using var log = Log();
        var wire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3 };
        await using var monitor = new DeviceMonitor(log);
        var mouse = monitor.RegisterDevice(wire, 0xFF, () => _now);
        await mouse.RefreshAsync(default);
        await mouse.SetHostAsync(1, default);
        Assert.Equal(Hex.Parse("10 FF 09 19 01 00 00"), wire.LastWrite);
        monitor.Process(wire, Hex.Parse("10 FF FF 09 19 08 00")); // BUSY, Solaar layout
        monitor.Process(wire, Frame("11 FF 09 19 00"));          // acknowledged before leaving (seen on a Unifying mouse)
        monitor.Process(wire, Frame("11 FF 09 1B 00"));          // another application's software id
        monitor.Process(wire, Frame("11 FF 09 09 03 00"));       // a getHostInfo reply is not a setCurrentHost reply
        var lines = Lines(log, "채널 전환 명령 응답");
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Contains("요청 채널 2") && line.Contains("오류 P2 C08 BUSY") && line.Contains("Solaar 배치") && line.Contains("ms"));
        Assert.Contains(lines, line => line.Contains("정상 응답"));
    }

    // ---- H4 / H17: a changed 0x1814 index and ignored events ----

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task KnownChangeHostIndexIsReplacedOnlyAfterTwoAgreeingLookups(bool genuine)
    {
        using var log = Log();
        var wire = new FakeWire("keyboard", receiver: false);
        var device = new HostDevice(wire, 0xFF, log, tickCount: () => _now);
        await device.RefreshAsync(default); Assert.Equal(9, device.Info.FeatureIndex);
        wire.NextFeatures.Enqueue(5); wire.NextFeatures.Enqueue(genuine ? 5 : 9);
        await device.RefreshAsync(default, true);
        Assert.Equal(genuine ? 5 : 9, device.Info.FeatureIndex);
        Assert.Equal(3, wire.Lookups); // one immediate re-lookup
        Assert.Single(Lines(log, "기존 0x09와 다름"));
        Assert.Single(Lines(log, genuine ? "위치 변경 확인: 0x09 → 0x05" : "직전의 다른 값 0x05는 반영하지 않음"));
    }

    [Fact]
    public async Task IgnoredEventIndexIsLoggedOnceAndCountedUntilTheLinkGoesDown()
    {
        using var log = Log();
        var wire = new FakeWire("keyboard");
        await using var monitor = new DeviceMonitor(log);
        var targets = Targets(monitor);
        var device = monitor.RegisterDevice(wire, 1, () => _now);
        await device.RefreshAsync(default);
        for (var i = 0; i < 3; i++) monitor.Process(wire, Event(1, 0x0F, 1));
        monitor.Process(wire, Event(1, 0x12, 0));
        Assert.Single(Lines(log, "feature 0x0F의 기기 이벤트"));
        Assert.Single(Lines(log, "feature 0x12의 기기 이벤트"));
        monitor.Process(wire, Hex.Parse("10 01 41 10 41 66 B3"));
        Assert.Single(Lines(log, "0x0F 3회, 0x12 1회"));
        Assert.Empty(targets);
    }

    // ---- H6: identical polling failures are collapsed, other failures are not ----

    [Fact]
    public void RepeatedLineIsWrittenOnceThenSummarizedPeriodicallyAndOnReset()
    {
        long now = 0; var gate = new RepeatedLogLine(() => now, 60_000);
        Assert.Equal(new[] { "a" }, gate.Next("a"));
        Assert.Empty(gate.Next("a"));
        now = 60_000;
        Assert.Equal(new[] { "a (같은 줄 2회 더 반복, 최근 60초)" }, gate.Next("a"));
        Assert.Empty(gate.Next("a"));
        Assert.Equal(new[] { "a (같은 줄 1회 더 반복, 최근 0초) 뒤 다른 결과로 바뀜", "b" }, gate.Next("b"));
        Assert.Null(gate.Reset("응답 받음"));
        Assert.Equal(new[] { "b" }, gate.Next("b"));
    }

    [Fact]
    public async Task RepeatedRootLookupErrorsOfADepartedDeviceAreCollapsedUntilAReply()
    {
        using var log = Log();
        var wire = new FakeWire("keyboard");
        var device = new HostDevice(wire, 1, log, tickCount: () => _now);
        await device.RefreshAsync(default); Assert.True(device.Info.Online);
        wire.LookupError = true;
        for (var i = 0; i < 3; i++) await device.RefreshAsync(default, true);
        Assert.Single(Lines(log, "HID++ 오류: 요청 feature 0x00, 함수 0x0 · P1 C08"));
        wire.LookupError = false;
        await device.RefreshAsync(default, true);
        Assert.Single(Lines(log, "같은 줄 2회 더 반복"));
        wire.HostError = true; // a non-Root failure (as in switch verification) stays one line per occurrence
        await device.ReadHostAsync(default); await device.ReadHostAsync(default);
        Assert.Equal(2, Lines(log, "HID++ 오류: 요청 feature 0x09, 함수 0x0").Length);
    }

    [Fact]
    public async Task RepeatedRootLookupTimeoutsAreCollapsedUntilAReply()
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        var answer = false;
        stream.Responder = payload => answer ? Frame("11 FF 00 09 09 00 00") : null;
        await using var connection = new HidConnection("silent", [(Bluetooth(stream.Name), stream)], log);
        connection.Start();
        var lookup = RootFeature.BuildGetFeature(0x1814, 0xFF, 9).ToBytes();
        Assert.Null(await connection.RequestAsync(lookup, default));
        Assert.Null(await connection.RequestAsync(lookup, default));
        answer = true;
        Assert.NotNull(await connection.RequestAsync(lookup, default));
        Assert.Single(Lines(log, "조회 시간 초과: feature 0x00"), line => !line.Contains("같은 줄"));
        Assert.Single(Lines(log, "같은 줄 1회 더 반복"));
    }

    // ---- H7: which HID++ error layout matched ----

    [Fact]
    public async Task ErrorLayoutIsLoggedOncePerDeviceAndLayoutAndNeverForAnAmbiguousRequest()
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"))
        {
            Responder = payload => (payload[2], payload[3], payload[5]) switch
            {
                (0x00, 0x09, 0x14) => Hex.Parse("10 FF FF 00 09 07 00"), // Root lookup 0x1814: Solaar layout
                (0x00, 0x09, 0x05) => Hex.Parse("10 FF FF 09 00 07 00"), // Root lookup 0x0005: swapped layout
                (0x09, 0x09, _) => Hex.Parse("10 FF FF 09 09 08 00"),    // feature 09 fn0 sw9: both layouts identical
                _ => null,
            },
        };
        await using var connection = new HidConnection("errors", [(Bluetooth(stream.Name), stream)], log);
        connection.Start();
        var changeHost = RootFeature.BuildGetFeature(0x1814, 0xFF, 9).ToBytes();
        Assert.True(HidppFrameParser.IsError((await connection.RequestAsync(changeHost, default))!));
        await connection.RequestAsync(changeHost, default);
        await connection.RequestAsync(RootFeature.BuildGetFeature(0x0005, 0xFF, 9).ToBytes(), default);
        await connection.RequestAsync(ChangeHostFeature.BuildGetHostInfo(0x09, 0xFF, 9).ToBytes(), default);
        var lines = Lines(log, "오류 바이트 배치 확인");
        Assert.Equal(2, lines.Length);
        Assert.Single(lines, line => line.Contains("Solaar 배치"));
        Assert.Single(lines, line => line.Contains("뒤집힌 배치"));
    }

    // ---- H19: a device arriving between the size and list calls is not a failed scan ----

    [Fact]
    public void InterfaceListThatGrowsBetweenTheTwoCallsIsReadAgainWithTheNewSize()
    {
        var sizes = new Queue<int>([12, 24]); var sizeCalls = 0;
        var result = Win32Hid.ReadInterfaceList(() => { sizeCalls++; return (0, sizes.Dequeue()); },
            buffer => { if (buffer.Length < 24) return Win32Hid.CrBufferSmall; "a#046d\0b#046d\0\0".CopyTo(0, buffer, 0, 15); return 0; });
        Assert.Equal(0, result.Status); Assert.Equal(new[] { "a#046d", "b#046d" }, result.Paths); Assert.Equal(2, sizeCalls);
    }

    [Fact]
    public void InterfaceListKeepsOtherFailuresAndStopsRetryingAGrowingList()
    {
        var other = Win32Hid.ReadInterfaceList(() => (0, 8), _ => 0x03);
        Assert.Equal((0x03, "조회"), (other.Status, other.Call));
        var calls = 0;
        var growing = Win32Hid.ReadInterfaceList(() => { calls++; return (0, 8); }, _ => Win32Hid.CrBufferSmall);
        Assert.Equal(Win32Hid.CrBufferSmall, growing.Status); Assert.Equal(5, calls); Assert.Empty(growing.Paths);
        Assert.Equal((0x1F, "크기 조회"), Win32Hid.ReadInterfaceList(() => (0x1F, 0), _ => 0) is var size ? (size.Status, size.Call) : default);
    }

    // ---- H9 / H10: receiver channels and notices ----

    [Fact]
    public void ReceiverWithoutLongInputIsNotTreatedAsConnectable()
    {
        HidInterface Receiver(int usage, int length) => new($"r{usage}", 0x046D, 0xC548, 0xFF00, usage, length, length, "Receiver");
        Assert.True(ReceiverChannels.MissingLongInput([Receiver(1, 7)]));
        Assert.False(ReceiverChannels.MissingLongInput([Receiver(1, 7), Receiver(2, 20)]));
        Assert.False(ReceiverChannels.MissingLongInput([Receiver(2, 20)]));
        Assert.False(ReceiverChannels.MissingLongInput([Bluetooth("bt")]));
    }

    [Fact]
    public async Task LongReportWithFeatureIndex40Or41NeverInvalidatesTheSelectedSlot()
    {
        using var log = Log();
        var wire = new FakeWire("keyboard");
        await using var monitor = new DeviceMonitor(log);
        var invalidated = 0; monitor.PairingInvalidated += _ => invalidated++;
        monitor.RegisterDevice(wire, 1, () => _now);
        monitor.Process(wire, Hex.Parse("10 01 41 10 01 66 B3")); // the device's real notice records its wireless id
        monitor.Process(wire, Frame("11 01 41 00 41 42 B0"));      // a 2.0 event of feature index 0x41
        monitor.Process(wire, Frame("11 01 40 00 02"));            // a 2.0 event of feature index 0x40
        Assert.Equal(0, invalidated);
        monitor.Process(wire, Hex.Parse("10 01 40 02 00 00 00"));  // the receiver's real unpairing notice
        Assert.Equal(1, invalidated);
    }

    // ---- H16: link up/down lines are written after anything a follow write could wait for ----

    [Fact]
    public async Task LinkUpLineFollowsTheStateChangeThatCanStartAFollowWrite()
    {
        using var log = Log();
        var wire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3 };
        var device = new HostDevice(wire, 0xFF, log, tickCount: () => _now);
        var seenAtChange = -1;
        device.Changed += changed => { if (seenAtChange < 0 && changed.Info.Online) seenAtChange = Lines(log, "이 PC 로컬 링크 올라옴").Length; };
        await device.RefreshAsync(default);
        Assert.Equal(0, seenAtChange);
        Assert.Single(Lines(log, "이 PC 로컬 링크 올라옴: Bluetooth 채널 조회 응답으로 확인(직전 링크 미확인)"));
    }

    [Fact]
    public async Task BluetoothTransportRemovalWritesOneLinkDownLineWithIgnoredEventCounts()
    {
        using var log = Log();
        var stream = new ScriptedStream(Path.Combine(_root, "hid.bin"));
        await using var connection = new HidConnection("bt-mouse", [(Bluetooth(stream.Name), stream)], log);
        await using var monitor = new DeviceMonitor(log);
        var device = monitor.RegisterDevice(connection, 0xFF);
        typeof(HostDevice).GetField("_info", Hidden)!.SetValue(device, device.Info with { Kind = "마우스", FeatureIndex = 9 });
        device.ReportLocalHost(new HostInfo(3, 0));
        monitor.Process(connection, Event(0xFF, 0x0F, 1));
        monitor.Process(connection, Event(0xFF, 0x0F, 1));
        typeof(DeviceMonitor).GetMethod("LogTransportGone", Hidden)!.Invoke(monitor, [connection, "HID 목록에서 빠짐"]);
        Assert.Single(Lines(log, "[Fixture · 마우스] Bluetooth HID 통로 사라짐(HID 목록에서 빠짐): 이 PC 링크 내려감"));
        Assert.Single(Lines(log, "0x0F 2회"));
    }

    // ---- H5 / H8 / H18: follow and manual paths ----

    [Fact]
    public Task KeyboardDepartureRefreshesAWaitingMouseOnceAndTheMouseFollows() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Model(log, keyWire, mouseWire, out var monitor, out var keyboard, out var mouse);
        await keyboard.RefreshAsync(default); await mouse.RefreshAsync(default);
        mouseWire.Silent = true; await mouse.RefreshAsync(default, true); mouseWire.Silent = false; // one idle query timed out
        Assert.False(mouse.Info.Online); Assert.True(mouse.LocalLinkConnected);
        Initialized(monitor)[keyWire.Key] = 1; Initialized(monitor)[mouseWire.Key] = 1;
        var lookups = mouseWire.Lookups;
        monitor.Process(keyWire, Event(0xFF, 9, 1));
        await Until(() => mouseWire.Writes == 1);
        Assert.Equal(1, mouseWire.LastWrite![4]);
        Assert.Equal(lookups + 1, mouseWire.Lookups);
        Assert.Single(Lines(log, "[Mouse] 따라 전환 대기: 상태를 한 번 즉시 다시 조회함"));
    });

    [Fact]
    public Task ManualSwitchLogsEachExcludedDeviceAfterTheWritesAreIssued() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3 };
        await using var vm = Model(log, keyWire, mouseWire, out _, out var keyboard, out _);
        await keyboard.RefreshAsync(default); // the mouse never answered
        var writesWhenLogged = -1;
        log.Added += entry => { if (entry.Message.Contains("수동 전환 대상에서 제외")) writesWhenLogged = keyWire.Writes; };
        await vm.SwitchAsync(1, "채널 버튼");
        Assert.Equal(1, keyWire.Writes); Assert.Equal(0, mouseWire.Writes);
        Assert.Single(Lines(log, "[Mouse] 채널 2 수동 전환 대상에서 제외: 채널 전환 기능(0x1814) 미확인"));
        Assert.Equal(1, writesWhenLogged);
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|B2|M1|SKIP_LINK|"));
    });

    [Fact]
    public Task ManualSwitchStillWritesToAKeyboardWhoseLastIdleQueryTimedOut() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Model(log, keyWire, mouseWire, out _, out var keyboard, out var mouse);
        await keyboard.RefreshAsync(default); await mouse.RefreshAsync(default);
        keyWire.Silent = true; await keyboard.RefreshAsync(default, true); keyWire.Silent = false; // measured 10-23: Online=false
        Assert.False(keyboard.Info.Online); Assert.True(keyboard.LocalLinkConnected); Assert.True(keyboard.TransportAvailable);
        await vm.SwitchAsync(1, "채널 버튼");
        Assert.Equal(1, mouseWire.Writes); Assert.Equal(1, keyWire.Writes);
        Assert.Equal(1, keyWire.LastWrite![4]);
        Assert.Empty(Lines(log, "수동 전환 대상에서 제외"));
    });

    [Fact]
    public Task MouseOwnEasySwitchEventsAreLoggedWithTheirChannelAndNeverFollow() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3 };
        await using var vm = Model(log, keyWire, mouseWire, out var monitor, out var keyboard, out var mouse);
        await keyboard.RefreshAsync(default); await mouse.RefreshAsync(default);
        monitor.Process(mouseWire, Event(0xFF, 9, 0)); // arrival-type report for its current channel
        monitor.Process(mouseWire, Event(0xFF, 9, 1)); // the mouse's own button: leaving for channel 2
        Assert.Single(Lines(log, "[Mouse] 도착 채널 1 알림: 출발로 처리하지 않음"));
        Assert.Single(Lines(log, "[Mouse] 전환 신호: 채널 2"));
        Assert.Single(Lines(log, "따라 전환 생략: 선택한 키보드가 아님"));
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("SKIP_KEY"));
        await Task.Delay(50);
        Assert.Equal(0, keyWire.Writes); Assert.Equal(0, mouseWire.Writes);
    });

    // ---- fixtures ----

    private AppLog Log() => new(Path.Combine(_root, "logs", Guid.NewGuid().ToString("N")));
    private static string[] Lines(AppLog log, string text) => log.Recent().Select(entry => entry.Message).Where(message => message.Contains(text)).ToArray();
    private static TaskCompletionSource Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ConcurrentQueue<int> Targets(DeviceMonitor monitor) { var targets = new ConcurrentQueue<int>(); monitor.HostChanged += (_, target) => targets.Enqueue(target); return targets; }
    private static byte[] Frame(string head) { var bytes = new byte[HidppConstants.LongLength]; Hex.Parse(head).CopyTo(bytes, 0); return bytes; }
    private static byte[] Event(int index, int feature, int target) { var bytes = new byte[HidppConstants.LongLength]; bytes[0] = 0x11; bytes[1] = (byte)index; bytes[2] = (byte)feature; bytes[4] = 0x02; bytes[5] = (byte)target; return bytes; }
    private static HidInterface Bluetooth(string path) => new(path, 0x046D, 0xB042, 0xFF43, 0x0202, 20, 20, "Fixture");
    private static void Closed(DeviceMonitor monitor, HidConnection connection) => typeof(DeviceMonitor).GetMethod("OnClosed", Hidden)!.Invoke(monitor, [connection]);
    private static ConcurrentDictionary<string, byte> Initialized(DeviceMonitor monitor) => (ConcurrentDictionary<string, byte>)typeof(DeviceMonitor).GetField("_initialized", Hidden)!.GetValue(monitor)!;

    private static HostDevice Ready(IDeviceTransport transport, AppLog log, string name, string kind)
    {
        var device = new HostDevice(transport, 0xFF, log);
        typeof(HostDevice).GetField("_info", Hidden)!.SetValue(device, device.Info with { Name = name, Kind = kind, FeatureIndex = 9 });
        typeof(HostDevice).GetField("_metadataRead", Hidden)!.SetValue(device, true);
        device.ReportLocalHost(new HostInfo(3, 0));
        return device;
    }

    private async Task<string> Seed(FakeWire wire, AppLog log)
    {
        await using var monitor = new DeviceMonitor(log, CacheDirectory);
        var device = monitor.RegisterDevice(wire, wire.Index, () => _now);
        monitor.ExpectDevices([device.Id]); await device.RefreshAsync(default);
        Assert.True(device.Info.Online); Assert.True(device.IdentityReady);
        return device.Id;
    }

    private MainViewModel Model(AppLog log, FakeWire keyWire, FakeWire mouseWire, out DeviceMonitor monitor, out HostDevice keyboard, out HostDevice mouse)
    {
        var keyboardId = new HostDevice(keyWire, 0xFF, log).Id; var mouseId = new HostDevice(mouseWire, 0xFF, log).Id;
        var store = new SettingsStore(Path.Combine(_root, "settings", Guid.NewGuid().ToString("N")));
        store.Save(new() { FollowEnabled = true, SelectedDeviceIds = [keyboardId, mouseId], SelectedDeviceNames = [new(keyboardId, keyWire.Name, "키보드"), new(mouseId, mouseWire.Name, "마우스")] });
        var vm = new MainViewModel(store, log, Dispatcher.CurrentDispatcher);
        monitor = (DeviceMonitor)typeof(MainViewModel).GetField("_monitor", Hidden)!.GetValue(vm)!;
        keyboard = monitor.RegisterDevice(keyWire, 0xFF, () => _now);
        mouse = monitor.RegisterDevice(mouseWire, 0xFF, () => _now);
        return vm;
    }

    private static async Task Until(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 2500;
        while (!condition()) { if (Environment.TickCount64 > end) throw new TimeoutException("Signal path fixture"); await Task.Delay(10); }
    }

    private static Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () => { try { await action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }

    private sealed class FakeWire(string key, bool receiver = true) : IDeviceTransport
    {
        private int _writes, _lookups;
        public string Key => key;
        public string Name { get; set; } = "Keyboard";
        public bool IsReceiver => receiver;
        public string ConnectionLabel => receiver ? "Logi Bolt" : "Bluetooth";
        public int Index => receiver ? 1 : 0xFF;
        public bool IsOpen { get; private set; } = true;
        public int SoftwareId => 9;
        public int Feature = 9, Kind;
        public int Host { get; init; }
        public bool Silent, LookupError, HostError, CloseOnWrite;
        public readonly ConcurrentQueue<int> NextFeatures = new();
        public readonly ConcurrentQueue<(int Feature, int Function)> Requests = new();
        public int? NameLookupReply;
        public int Writes => Volatile.Read(ref _writes);
        public int Lookups => Volatile.Read(ref _lookups);
        public byte[]? LastWrite;
        public TaskCompletionSource? TypeHold, CountHold;
        public readonly TaskCompletionSource TypeEntered = Hold(), CountEntered = Hold();

        public async Task<byte[]?> RequestAsync(byte[] raw, CancellationToken cancel)
        {
            var request = HidppFrameParser.Parse(raw)!;
            Requests.Enqueue((request.FeatureIndex, request.FunctionId));
            if (Silent) return null;
            if (request.FeatureIndex == 0 && request.Param(0) == 0x18)
            {
                Interlocked.Increment(ref _lookups);
                if (LookupError) return [0x10, (byte)request.DeviceIndex, 0x8F, 0x00, (byte)request.FuncSwByte, 0x08, 0x00];
                return Reply(request, [(byte)(NextFeatures.TryDequeue(out var next) ? next : Feature), 0, 0]);
            }
            if (request.FeatureIndex == 0 && request.Param(1) == 5) return Reply(request, [(byte)(NameLookupReply ?? 20), 0, 0]);
            if (request.FeatureIndex == 20)
            {
                switch (request.FunctionId)
                {
                    case 2: TypeEntered.TrySetResult(); if (TypeHold is not null) await TypeHold.Task.WaitAsync(cancel); return Reply(request, [(byte)Kind, 0, 0]);
                    case 0: CountEntered.TrySetResult(); if (CountHold is not null) await CountHold.Task.WaitAsync(cancel); return Reply(request, [(byte)Encoding.UTF8.GetByteCount(Name), 0, 0]);
                    case 1: return Reply(request, Encoding.UTF8.GetBytes(Name).Skip(request.Param(0)).Take(16).ToArray());
                }
            }
            if (request.FunctionId == 0)
                return HostError ? [0x10, (byte)request.DeviceIndex, 0xFF, (byte)request.FeatureIndex, (byte)request.FuncSwByte, 0x05, 0x00] : Reply(request, [3, (byte)Host, 0]);
            if (request.FunctionId is 1 or 2) return Reply(request, [0, 0, 0]); // any other read on the 0x1814 index
            throw new InvalidOperationException("Unexpected fixture query: " + request);
        }

        private static byte[] Reply(HidppFrame request, byte[] parameters) =>
            HidppFrame.Build(request.FeatureIndex, request.FunctionId, parameters.Length > 3 ? ReportType.Long : ReportType.Short, request.DeviceIndex, request.SoftwareId, parameters).ToBytes();

        public Task SendAsync(byte[] request, CancellationToken cancel)
        {
            LastWrite = request; Interlocked.Increment(ref _writes);
            if (CloseOnWrite) IsOpen = false;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedStream(string path) : FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private int _writes;
        public Func<byte[], byte[]?>? Responder;
        public Action<int>? OnWrite;
        public int Writes => Volatile.Read(ref _writes);
        public void Fail() => _input.Writer.TryComplete(new IOException("fixture device removed"));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var frame = await _input.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            frame.CopyTo(buffer); return frame.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _writes);
            if (Responder?.Invoke(buffer.ToArray()) is { } reply) _input.Writer.TryWrite(reply);
            OnWrite?.Invoke(count);
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EasySwitch.Tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_root);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe signal path fixture cleanup");
        try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
