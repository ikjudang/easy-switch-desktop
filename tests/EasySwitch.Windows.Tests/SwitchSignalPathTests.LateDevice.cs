using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using EasySwitch.Core;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

// The macOS late manual wait (f937016) on Windows: a channel request waits up to 3 s for a selected device
// that is not connected yet and sends it the same channel once. Each test names the macOS test it follows.
public sealed partial class SwitchSignalPathTests
{
    // macOS lateManualSwitchWaitsForKeyboardAfterEasySwitchKeyDeparture
    [Fact]
    public Task KeyboardReturningAfterItsEasySwitchKeyGetsTheChannelRequestedWhileItWasAway() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { Name = "MX KEYS S" };
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var keyboard = await Arrive(monitor, keyWire);
        monitor.Process(keyWire, Event(0xFF, 9, 2));          // the Easy-Switch key: leaving for channel 3
        Assert.Single(Lines(log, "[MX KEYS S] 전환 신호: 채널 3"));
        Leave(keyboard, keyWire);
        var back = new FakeWire("keyboard", receiver: false) { Name = "MX KEYS S", CloseOnWrite = true };
        var request = vm.SwitchAsync(2, SwitchRequestSource.Floating);
        await Task.Delay(500);
        Assert.Equal(0, back.Writes);
        await Arrive(monitor, back);
        await request;
        Assert.Equal(1, back.Writes); Assert.Equal(2, back.LastWrite![4]);
        Assert.Single(Lines(log, "[MX KEYS S] 키보드 복귀: 수동 전환이 이 키보드의 연결을 기다리는 중이라 진행 중인 전환을 취소하지 않음"));
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|P3|K1|WAIT|"));
    });

    // macOS lateManualSwitchWaitsForKeyboardAfterDepartureEcho
    [Fact]
    public Task KeyboardThatLeftWithTheEchoOfAManualSwitchIsWaitedForByTheNextRequest() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire, mouseWire);
        await Arrive(monitor, keyWire); await Arrive(monitor, mouseWire);
        keyWire.AfterSend = write => monitor.Process(keyWire, Event(0xFF, 9, write[4]));
        await vm.SwitchAsync(2, SwitchRequestSource.Floating);
        Assert.Equal(1, keyWire.Writes); Assert.Equal(1, mouseWire.Writes);
        Assert.Single(Lines(log, "따라 전환 생략: 앱에서 같은 채널을 직접 전환하는 중"));
        var mouseBack = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await Arrive(monitor, mouseBack);
        var keyBack = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        var request = vm.SwitchAsync(2, SwitchRequestSource.Floating);
        await Task.Delay(500);
        Assert.Equal(1, mouseBack.Writes); Assert.Equal(0, keyBack.Writes);
        await Arrive(monitor, keyBack);
        await request;
        Assert.Equal(1, keyBack.Writes); Assert.Equal(2, keyBack.LastWrite![4]);
    });

    // macOS manualSwitchSendsOnceToKeyboardArrivingOneSecondLater
    [Fact]
    public Task ManualSwitchSendsOnceToAKeyboardArrivingOneSecondLater() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var id = Id(keyWire, log); var notices = Notices(vm);
        var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
        Assert.Single(Lines(log, "[Keyboard] 채널 2 수동 전환 대기: 선택한 기기가 아직 이 PC에 연결되지 않아 최대 3초 기다림"));
        await Until(() => Rows(notices, id).Length > 0);
        Assert.All(Rows(notices, id), row => Assert.Equal(DeviceNoticeState.Switching, row.State));
        await Task.Delay(1000);
        await Arrive(monitor, keyWire);
        await request;
        Assert.Equal(1, keyWire.Writes); Assert.Equal(1, keyWire.LastWrite![4]);
        await Until(() => Rows(notices, id).Last().State == DeviceNoticeState.Departed);
        Assert.Equal("완료", EasySwitch.Windows.Notices.NoticeBubble.Status(Rows(notices, id).Last(), false).Text);
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|B2|K1|WAIT|"));
    });

    // macOS manualSwitchExpiresAfterThreeSecondsWithoutKeyboardAndDoesNotReplay
    [Fact]
    public Task ManualWaitExpiresAfterThreeSecondsAndAKeyboardArrivingLaterGetsNothing() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var id = Id(keyWire, log); var notices = Notices(vm);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await vm.SwitchAsync(1, SwitchRequestSource.Command);
        Assert.True(clock.ElapsedMilliseconds >= 3000, $"returned after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(0, keyWire.Writes);
        Assert.Single(Lines(log, "[Keyboard] 채널 2 수동 전환 대기 만료: 3초 안에 연결되지 않음"));
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|B2|K1|CAN_EXP|"));
        await Until(() => Rows(notices, id).Last().State == DeviceNoticeState.Unconfirmed);
        await Arrive(monitor, keyWire);
        await Task.Delay(150);
        Assert.Equal(0, keyWire.Writes);
    });

    // macOS disabledLateDeviceSwitchSkipsAbsentKeyboard
    [Fact]
    public Task WithTheSettingOffAnAbsentKeyboardIsSkippedAtOnce() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        await using var vm = Selected(log, out var monitor, false, keyWire);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await vm.SwitchAsync(1, SwitchRequestSource.Command);
        Assert.True(clock.ElapsedMilliseconds < 1000);
        await Arrive(monitor, keyWire);
        await Task.Delay(150);
        Assert.Equal(0, keyWire.Writes);
        Assert.Single(Lines(log, "[Keyboard] 채널 2 수동 전환 대상에서 제외: 이 PC에서 기기 통로를 찾지 못함. 미연결 기기 연결 대기가 꺼져 있어 기다리지 않음"));
        Assert.DoesNotContain(log.RecentCodes(), code => code.Code.Contains("|WAIT|"));
    });

    // macOS newerManualRequestCancelsLateWaitAndOnlySendsNewDestination
    [Fact]
    public Task NewerManualRequestEndsTheEarlierWaitAndOnlyItsChannelIsSent() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var first = vm.SwitchAsync(1, SwitchRequestSource.Command);
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|R0001|B2|K1|WAIT|"));
        var second = vm.SwitchAsync(2, SwitchRequestSource.Command);
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|R0002|B3|K1|WAIT|"));
        await first;
        Assert.Single(Lines(log, "[Keyboard] 채널 2 수동 전환 대기 취소: 더 최근 요청 또는 설정 변경"));
        await Arrive(monitor, keyWire);
        await second;
        Assert.Equal(1, keyWire.Writes); Assert.Equal(2, keyWire.LastWrite![4]);
    });

    // macOS lateKeyboardDepartureEchoDoesNotStartAnotherMouseFollow
    [Fact]
    public Task LateKeyboardsDepartureEchoDoesNotStartAnotherMouseFollow() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire, mouseWire);
        var mouseId = Id(mouseWire, log); var notices = Notices(vm);
        await Arrive(monitor, mouseWire);
        keyWire.AfterSend = write => monitor.Process(keyWire, Event(0xFF, 9, write[4]));
        var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
        await Until(() => mouseWire.Writes == 1);
        await Arrive(monitor, keyWire);
        await request;
        Assert.Equal(1, keyWire.Writes); Assert.Equal(1, keyWire.LastWrite![4]);
        Assert.Equal(1, mouseWire.Writes);
        Assert.Single(Lines(log, "따라 전환 생략: 앱에서 같은 채널을 직접 전환하는 중"));
        Assert.Empty(Lines(log, "10초 만료"));
        await Task.Delay(100);
        Assert.DoesNotContain(Rows(notices, mouseId), row => row.State is DeviceNoticeState.Unconfirmed or DeviceNoticeState.Unavailable or DeviceNoticeState.Cancelled or DeviceNoticeState.WaitExpired);
    });

    // macOS disabledLateDeviceSwitchExcludesPreparingMouseWithoutNoticeOrDeferredWrite. Windows shows every
    // selected device in a follow notice from the start, so the excluded mouse ends as "확인 필요" there.
    [Fact]
    public Task WithTheSettingOffAFollowLeavesOutAMouseThatIsStillPreparing() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, false, keyWire, mouseWire);
        var mouseId = Id(mouseWire, log); var notices = Notices(vm);
        await Arrive(monitor, keyWire);
        var mouse = monitor.RegisterDevice(mouseWire, 0xFF, () => _now);   // handle open, nothing answered yet
        monitor.Process(keyWire, Event(0xFF, 9, 1));
        Assert.Single(Lines(log, "[Mouse] 따라 전환 대상 제외: 마우스가 아직 준비되지 않았고 미연결 기기 연결 대기가 꺼져 있어 기다리지 않음"));
        await mouse.RefreshAsync(default); Assert.True(mouse.Info.Online);
        await Task.Delay(150);
        Assert.Equal(0, mouseWire.Writes);
        Assert.Empty(Lines(log, "마우스 복귀 대기"));
        await Until(() => Rows(notices, mouseId).LastOrDefault()?.State == DeviceNoticeState.Unavailable);
    });

    [Fact]
    public Task WithTheSettingOnTheSamePreparingMouseIsStillWaitedForAndFollows() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire, mouseWire);
        await Arrive(monitor, keyWire);
        var mouse = monitor.RegisterDevice(mouseWire, 0xFF, () => _now);
        monitor.Process(keyWire, Event(0xFF, 9, 1));
        Assert.Single(Lines(log, "마우스 복귀 대기"));
        await mouse.RefreshAsync(default);
        await Until(() => mouseWire.Writes == 1);
        Assert.Equal(1, mouseWire.LastWrite![4]);
    });

    // macOS serviceConfigurationWithoutLateDeviceFieldDefaultsToEnabled
    [Fact]
    public void SettingsAndServicePreferencesWithoutTheFieldMeanOn()
    {
        Assert.True(JsonSerializer.Deserialize<AppSettings>("""{"FollowEnabled":true}""")!.LateDeviceSwitchEnabled);
        Assert.True(new AppSettings().LateDeviceSwitchEnabled);
        var older = Older(EnginePreferences.From(new AppSettings()));
        Assert.Null(older.LateDeviceSwitchEnabled);
        Assert.True(older.Apply(new AppSettings()).LateDeviceSwitchEnabled);
    }

    // macOS serviceConfigurationRoundTripsLateDeviceField
    [Theory]
    [InlineData(true)] [InlineData(false)] [InlineData(null)]
    public void ServicePreferencesRoundTripTheField(bool? enabled)
    {
        var original = EnginePreferences.From(new AppSettings { FollowEnabled = true, SelectedDeviceIds = ["k", "m"] }) with { LateDeviceSwitchEnabled = enabled };
        var decoded = JsonSerializer.Deserialize<EnginePreferences>(JsonSerializer.Serialize(original))!;
        Assert.Equal(enabled, decoded.LateDeviceSwitchEnabled);
        Assert.Equal(original.SelectedDeviceIds, decoded.SelectedDeviceIds); Assert.True(decoded.FollowEnabled);
    }

    // Windows: a service or app from before the setting neither stores nor reports it. Its gap must not turn
    // the other side's "off" back on, and must not count as a difference that is saved again on every poll.
    [Fact]
    public void APeerWithoutTheFieldKeepsTheLocalValueAndComparesEqual()
    {
        var local = new AppSettings { LateDeviceSwitchEnabled = false, FollowEnabled = true };
        var older = Older(EnginePreferences.From(local));
        Assert.False(older.Apply(local).LateDeviceSwitchEnabled);
        Assert.Equal(EnginePreferences.From(local).Fingerprint, older.WithLateDefault(local.LateDeviceSwitchEnabled).Fingerprint);
        Assert.True(EnginePreferences.From(local).WithLateDefault(true).LateDeviceSwitchEnabled == false); // a sent value is never replaced
    }

    [Fact]
    public Task TheSettingIsSavedAndReadBack() => Sta(async () =>
    {
        using var log = Log();
        var directory = Path.Combine(_root, "settings", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(directory);
        await using (var vm = new MainViewModel(store, log, Dispatcher.CurrentDispatcher))
        {
            Assert.True(vm.LateDeviceSwitchEnabled);
            vm.LateDeviceSwitchEnabled = false;
            Assert.Single(Lines(log, "미연결 기기 연결 대기(최대 3초) 끔"));
        }
        Assert.False(new SettingsStore(directory).Value.LateDeviceSwitchEnabled);
    });

    // macOS lateManualSwitchUsesTheSameDestinationFromFloatingAndTrayButtons
    [Theory]
    [InlineData(SwitchRequestSource.Floating, "P")] [InlineData(SwitchRequestSource.Tray, "T")]
    public Task FloatingAndTrayRequestsWaitForTheSameDestination(string source, string letter) => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var request = vm.SwitchAsync(2, source);
        Assert.Contains(log.RecentCodes(), code => code.Code.Contains($"|{letter}3|K1|WAIT|"));
        await Arrive(monitor, keyWire);
        await request;
        Assert.Equal(1, keyWire.Writes); Assert.Equal(2, keyWire.LastWrite![4]);
    });

    // macOS lateManualNoticeUsesDeviceKindWhenTheNameIsUnknown
    [Theory]
    [InlineData("키보드")] [InlineData("마우스")]
    public Task WaitingRowUsesTheDeviceKindWhenTheNameIsUnknown(string kind) => Sta(async () =>
    {
        using var log = Log();
        var store = new SettingsStore(Path.Combine(_root, "settings", Guid.NewGuid().ToString("N")));
        store.Save(new() { FollowEnabled = true, KeyboardId = kind == "키보드" ? "unknown" : null, MouseId = kind == "마우스" ? "unknown" : null });
        await using var vm = new MainViewModel(store, log, Dispatcher.CurrentDispatcher);
        var notices = Notices(vm);
        var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
        await Until(() => Rows(notices, "unknown").Length > 0);
        var row = Rows(notices, "unknown")[0];
        Assert.Equal((kind, kind, DeviceNoticeState.Switching), (row.Name, row.Kind, row.State));
        vm.LateDeviceSwitchEnabled = false;
        await request;
        await Until(() => Rows(notices, "unknown").Last().State == DeviceNoticeState.Unconfirmed);
    });

    // macOS lateManualWaitCancellationNeverReplaysWhenTheKeyboardArrives ("task" becomes a Windows sleep)
    [Theory]
    [InlineData("selection", "기기 선택 변경")] [InlineData("toggle", "연결 대기 설정 꺼짐")]
    [InlineData("stop", "앱 종료")] [InlineData("power", "절전 전환 처리 중")]
    public Task CancelledWaitNeverSendsWhenTheKeyboardArrivesLater(string reason, string logged) => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var vm = Selected(log, out var monitor, true, keyWire);
        var id = Id(keyWire, log);
        try
        {
            var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
            Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|WAIT|"));
            switch (reason)
            {
                case "selection": vm.ToggleDevice(new DeviceInfo(id, "Keyboard", "", "키보드", false, 0, null, 0)); break;
                case "toggle": vm.LateDeviceSwitchEnabled = false; break;
                case "stop": await vm.DisposeAsync(); break;
                default: await vm.ChangePowerAsync(true, "fixture suspend"); break;
            }
            await request;
            Assert.Single(Lines(log, $"[Keyboard] 채널 2 수동 전환 대기 취소: {logged}. 이 요청의 채널은 나중에 보내지 않음"));
            Assert.Contains(log.RecentCodes(), code => code.Code.Contains("|K1|CAN_PREP|"));
            if (reason != "stop") await Arrive(monitor, keyWire);
            await Task.Delay(150);
            Assert.Equal(0, keyWire.Writes);
        }
        finally { if (reason != "stop") await vm.DisposeAsync(); }
    });

    // macOS lateManualWaitRetainsKnownNameAcrossBluetoothReattachment
    [Fact]
    public Task WaitingRowKeepsTheKnownNameAcrossABluetoothReattachment() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false) { Name = "MX KEYS S" };
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var id = Id(keyWire, log); var notices = Notices(vm);
        Leave(await Arrive(monitor, keyWire), keyWire);
        var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
        await Until(() => Rows(notices, id).Length > 0);
        Assert.Equal(("MX KEYS S", DeviceNoticeState.Switching), (Rows(notices, id)[0].Name, Rows(notices, id)[0].State));
        var returned = new FakeWire("keyboard", receiver: false) { Name = "MX KEYS S", CloseOnWrite = true };
        await Arrive(monitor, returned);
        await request;
        Assert.Equal(1, returned.Writes); Assert.Equal(1, returned.LastWrite![4]);
    });

    // macOS lateManualDeadlineIncludesTimeSpentPresentingTheInitialNotice
    [Fact]
    public Task DeadlineCountsFromTheRequestIncludingTimeSpentShowingTheFirstNotice() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        await using var vm = Selected(log, out var monitor, true, keyWire);
        var first = true;
        vm.NoticeRequested += _ =>
        {
            if (!first) return; first = false;
            Task.Run(() => Arrive(monitor, keyWire)).GetAwaiter().GetResult();   // the keyboard connects meanwhile
            Thread.Sleep(3100);                                                 // the first notice takes 3.1 s to show
        };
        await vm.SwitchAsync(1, SwitchRequestSource.Command);
        Assert.Equal(0, keyWire.Writes);
        Assert.Single(Lines(log, "수동 전환 대기 만료: 3초 안에 연결되지 않음"));
    });

    // macOS keyboardDepartureCancelsItsLateManualWait. Leaving for the requested channel ends the wait as a
    // departure; leaving for another channel is a newer keyboard signal that ends it as well.
    [Theory]
    [InlineData(1, "기기 출발 신호")] [InlineData(2, "더 최근 요청 또는 설정 변경")]
    public Task DepartureOfTheAwaitedKeyboardEndsItsWait(int departure, string logged) => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire, mouseWire);
        await Arrive(monitor, mouseWire);
        var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
        await Until(() => mouseWire.Writes == 1);
        var keyboard = monitor.RegisterDevice(keyWire, 0xFF, () => _now);   // link up, its channel reply not answered yet
        typeof(HostDevice).GetField("_info", Hidden)!.SetValue(keyboard, keyboard.Info with { Name = "Keyboard", Kind = "키보드", FeatureIndex = 9, HostCount = 3, CurrentHost = 0 });
        await Task.Delay(250);
        Assert.Equal(0, keyWire.Writes);
        monitor.Process(keyWire, Event(0xFF, 9, departure));
        await request;
        Assert.Equal(0, keyWire.Writes);
        Assert.Single(Lines(log, $"[Keyboard] 채널 2 수동 전환 대기 취소: {logged}"));
    });

    // Windows constraint: a ready device's write is never delayed by deciding which absent devices to wait for.
    [Fact]
    public Task ReadyMouseIsWrittenBeforeTheAbsentKeyboardsWaitIsRecorded() => Sta(async () =>
    {
        using var log = Log();
        var keyWire = new FakeWire("keyboard", receiver: false);
        var mouseWire = new FakeWire("mouse", receiver: false) { Name = "Mouse", Kind = 3, CloseOnWrite = true };
        await using var vm = Selected(log, out var monitor, true, keyWire, mouseWire);
        await Arrive(monitor, mouseWire);
        var writesWhenWaitLogged = -1;
        log.Added += entry => { if (entry.Message.Contains("수동 전환 대기:")) writesWhenWaitLogged = mouseWire.Writes; };
        var request = vm.SwitchAsync(1, SwitchRequestSource.Command);
        Assert.Equal(1, writesWhenWaitLogged);
        vm.LateDeviceSwitchEnabled = false;
        await request;
    });

    // ---- late-device fixtures ----

    private MainViewModel Selected(AppLog log, out DeviceMonitor monitor, bool late, params FakeWire[] wires)
    {
        var ids = wires.Select(wire => Id(wire, log)).ToArray();
        var store = new SettingsStore(Path.Combine(_root, "settings", Guid.NewGuid().ToString("N")));
        store.Save(new() { FollowEnabled = true, LateDeviceSwitchEnabled = late, SelectedDeviceIds = ids,
            SelectedDeviceNames = wires.Select((wire, i) => new DeviceInfoCache(ids[i], wire.Name, wire.Kind == 3 ? "마우스" : "키보드")).ToArray() });
        var vm = new MainViewModel(store, log, Dispatcher.CurrentDispatcher);
        monitor = (DeviceMonitor)typeof(MainViewModel).GetField("_monitor", Hidden)!.GetValue(vm)!;
        return vm;
    }
    private static string Id(FakeWire wire, AppLog log) => new HostDevice(wire, wire.Index, log).Id;
    private async Task<HostDevice> Arrive(DeviceMonitor monitor, FakeWire wire)
    {
        var device = monitor.RegisterDevice(wire, wire.Index, () => _now);
        await device.RefreshAsync(default);
        return device;
    }
    private static void Leave(HostDevice device, FakeWire wire) { wire.IsOpen = false; device.MarkLinkClosed(); }
    private static ConcurrentQueue<SwitchNotice> Notices(MainViewModel vm) { var notices = new ConcurrentQueue<SwitchNotice>(); vm.NoticeRequested += notices.Enqueue; return notices; }
    private static DeviceResult[] Rows(ConcurrentQueue<SwitchNotice> notices, string id) => notices.SelectMany(notice => notice.Devices).Where(row => row.Id == id).ToArray();
    // The same preferences as a version before the setting existed would send them.
    private static EnginePreferences Older(EnginePreferences preferences)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(preferences))!.AsObject();
        Assert.True(json.Remove("LateDeviceSwitchEnabled"));
        return JsonSerializer.Deserialize<EnginePreferences>(json.ToJsonString())!;
    }
}
