using System.IO;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class ReceiverRoutingTests
{
    [Theory]
    [InlineData(0xC548,"10 01 41 10 41 66 B3")]
    [InlineData(0xC52B,"10 01 41 04 52 90 40")]
    public async Task FreshHostQueryRecoversInitialOfflineNoticeWithoutChannelRoundTrip(int product,string offline)
    {
        using var fixture=new Fixture();await using var connection=fixture.Connection(product);
        var device=new HostDevice(connection,1,fixture.Log);var arrivals=0;device.LinkArrived+=_=>arrivals++;
        device.ApplyConnectionNotice(ReceiverMessages.ParseDeviceConnection(Hex.Parse(offline))!);
        Assert.False(device.LocalLinkConnected);
        var query=device.HostQueryRevision;
        Assert.True(device.ReportLocalHost(new HostInfo(3,0),query));
        Assert.True(device.Info.Online);Assert.True(device.LocalLinkConnected);Assert.Equal(0,device.CurrentHost);Assert.Equal(1,arrivals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryStartedBeforeDepartureOrOfflineNoticeCannotRestoreOnline(bool departure)
    {
        using var fixture=new Fixture();await using var connection=fixture.Connection(0xC548);
        var device=new HostDevice(connection,1,fixture.Log);device.ReportLocalHost(new HostInfo(3,0));
        var query=device.HostQueryRevision;
        if(departure){device.NoteDepartureSignal();device.MarkOffline();}else device.MarkLinkClosed();
        Assert.False(device.ReportLocalHost(new HostInfo(3,0),query));Assert.False(device.Info.Online);
        if(departure)Assert.False(device.ReportLocalHost(new HostInfo(3,0),device.HostQueryRevision));
    }

    [Fact]
    public async Task LateHostResponseAfterDepartureCannotUndoTheLatestFollowIntent()
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(0xC548);
        var device = new HostDevice(connection, 3, fixture.Log);
        var arrivals = 0;
        device.LinkArrived += _ => arrivals++;
        device.ReportLocalHost(new HostInfo(3, 2));
        device.NoteDepartureSignal();
        device.MarkOffline();
        Assert.False(device.ReportLocalHost(new HostInfo(3, 2)));
        Assert.False(device.Info.Online);
        Assert.Equal(1, arrivals);
        device.ApplyConnectionNotice(ReceiverMessages.ParseDeviceConnection(Hex.Parse("10 03 41 10 41 66 B3"))!);
        device.ApplyConnectionNotice(ReceiverMessages.ParseDeviceConnection(Hex.Parse("10 03 41 10 01 66 B3"))!);
        Assert.True(device.ReportLocalHost(new HostInfo(3, 2)));
        Assert.Equal(2, arrivals);
        Assert.True(device.Info.Online);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedEventHandlerDoesNotSilentlyStopTheInputLoop(bool cancellationException)
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(0xC548, input: Hex.Parse("10 03 41 10 01 66 B3 10 03 41 10 41 66 B3"));
        var received = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Received += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == 1)
            {
                if (cancellationException) throw new OperationCanceledException("operation timeout, not app shutdown");
                throw new InvalidDataException("bad notification handler");
            }
            second.TrySetResult();
        };
        connection.Start();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, received);
    }

    [Theory]
    [InlineData(0xC548, "Logi Bolt")]
    [InlineData(0xC52B, "Unifying")]
    [InlineData(0xFFFF, "USB")]
    public async Task ReceiverKindsAreNotAllLabeledAsBolt(int product, string label)
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(product);
        Assert.Equal(label, connection.ConnectionLabel);
    }

    [Theory]
    [InlineData(0xC548, "10 03 41 10 01 66 B3", "10 03 41 10 41 66 B3")]
    [InlineData(0xC52B, "10 02 41 04 92 90 40", "10 02 41 04 52 90 40")]
    public async Task WirelessReturnCreatesNewGenerationWithoutClosingUsbReceiver(int product, string on, string off)
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(product);
        var online = ReceiverMessages.ParseDeviceConnection(Hex.Parse(on))!;
        var offline = ReceiverMessages.ParseDeviceConnection(Hex.Parse(off))!;
        var device = new HostDevice(connection, online.DeviceIndex, fixture.Log);
        var arrivals = 0;
        device.LinkArrived += _ => arrivals++;
        device.ApplyConnectionNotice(online);
        device.ApplyConnectionNotice(online); // Other apps can request the same connection notification again.
        Assert.Equal(1, arrivals);
        device.NoteSwitchStarted();
        Assert.True(device.SwitchGraceActive);
        device.ApplyConnectionNotice(offline);
        Assert.False(device.LocalLinkConnected);
        Assert.True(connection.IsOpen);
        device.ApplyConnectionNotice(online);
        Assert.Equal(2, arrivals);
        Assert.Equal(2, device.ConnectionGeneration);
        Assert.False(device.SwitchGraceActive);
    }

    [Fact]
    public async Task QueryTimeoutAndRecoveryDoesNotInventKeyboardArrivalAndCancelFollow()
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(0xC548);
        var device = new HostDevice(connection, 3, fixture.Log);
        var arrivals = 0;
        device.LinkArrived += _ => arrivals++;
        device.ReportLocalHost(new HostInfo(3, 2));
        device.MarkOffline(); // A failed HID++ query is not a wireless disconnect notification.
        device.ReportLocalHost(new HostInfo(3, 2));
        Assert.Equal(1, arrivals);
        Assert.Equal(1, device.ConnectionGeneration);
        Assert.True(device.LocalLinkConnected);
    }

    [Fact]
    public async Task DirectBluetoothKeepsKnownRouteForEventsArrivingBeforeFreshQueries()
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(0xB366, bluetooth: true);
        var initial = new HostDevice(connection, 0xFF, fixture.Log);
        var known = initial.Info with { Name = "MX Mechanical", FeatureIndex = 9, HostCount = 3, CurrentHost = 2, Online = true };
        var returned = new HostDevice(connection, 0xFF, fixture.Log, known);
        Assert.Equal(9, returned.Info.FeatureIndex);
        Assert.Equal(2, returned.CurrentHost);
        Assert.False(returned.Info.Online);
    }

    [Fact]
    public async Task NewReceiverHandleDoesNotTrustCachedSlotRouteUntilTheDeviceIsQueried()
    {
        using var fixture = new Fixture();
        await using var connection = fixture.Connection(0xC548);
        var initial = new HostDevice(connection, 3, fixture.Log);
        var known = initial.Info with { FeatureIndex = 9, HostCount = 3, CurrentHost = 2, Online = true };
        var returned = new HostDevice(connection, 3, fixture.Log, known);
        Assert.Equal(0, returned.Info.FeatureIndex);
        Assert.Null(returned.CurrentHost);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "EasySwitch.Tests", Guid.NewGuid().ToString("N"));
        public AppLog Log { get; }
        public Fixture() => Log = new AppLog(_path);
        public HidConnection Connection(int product, bool bluetooth = false, byte[]? input = null)
        {
            var path = Path.Combine(_path, Guid.NewGuid().ToString("N") + ".bin");
            var length = bluetooth ? 20 : 7;
            var info = new HidInterface(path, 0x046D, product, bluetooth ? 0xFF43 : 0xFF00, bluetooth ? 0x0202 : 1, length, length, "Test HID");
            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 0, FileOptions.Asynchronous);
            if (input is not null) { stream.Write(input); stream.Position = 0; }
            return new HidConnection("fixture", [(info, stream)], Log);
        }
        public void Dispose()
        {
            Log.Dispose();
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EasySwitch.Tests")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(_path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(target, true);
        }
    }
}
