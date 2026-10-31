using System.IO;
using System.Reflection;
using EasySwitch.Core;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Notices;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class HidDisconnectNoticeTests
{
    [Fact]
    public async Task DisconnectedVerificationWriteCompletesLocalToastBeforeTheReaderReportsClosure()
    {
        await using var fixture = new Fixture(successfulWrites: 1, error: unchecked((int)0x8007048F));
        var outcome = await fixture.SwitchAsync();
        Assert.Equal(SwitchOutcome.LocalLinkClosed, outcome);
        Assert.False(fixture.Connection.IsOpen);
        Assert.Equal(1, fixture.Closures);
        Assert.Equal(2, fixture.Stream.Writes); // One command and one verification; no stale retries.
        var row = DeviceResult.FromOutcome(fixture.Device.Id, "Keyboard", "키보드", outcome);
        Assert.Equal("완료", NoticeBubble.Status(row, false).Text);
        Assert.Null(row.Success); // Local departure still does not prove destination arrival.
    }

    [Fact]
    public async Task DisconnectBeforeAnyCommandIsWrittenDoesNotClaimCompletedSwitch()
    {
        await using var fixture = new Fixture(successfulWrites: 0, error: unchecked((int)0x8007048F));
        var outcome = await fixture.SwitchAsync();
        Assert.Equal(SwitchOutcome.Unverified, outcome);
        Assert.False(fixture.Connection.IsOpen);
        Assert.Equal(1, fixture.Closures);
        Assert.Equal(1, fixture.Stream.Writes);
        Assert.Equal("확인 필요", NoticeBubble.Status(DeviceResult.FromOutcome("k", "Keyboard", "키보드", outcome), false).Text);
    }

    [Fact]
    public async Task GenericIoFailureKeepsTheLinkOpenAndTheToastUnconfirmed()
    {
        await using var fixture = new Fixture(successfulWrites: 1, error: unchecked((int)0x8007001F));
        var outcome = await fixture.SwitchAsync();
        Assert.Equal(SwitchOutcome.Unverified, outcome);
        Assert.True(fixture.Connection.IsOpen);
        Assert.Equal(0, fixture.Closures);
        Assert.Equal(4, fixture.Stream.Writes);
        Assert.Equal("확인 필요", NoticeBubble.Status(DeviceResult.FromOutcome("k", "Keyboard", "키보드", outcome), false).Text);
    }

    // No reader is started: the terminal write must update state without a read callback.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "EasySwitch.Tests", Guid.NewGuid().ToString("N"));
        private readonly AppLog _log;
        public ErrorStream Stream { get; }
        public HidConnection Connection { get; }
        public HostDevice Device { get; }
        public int Closures { get; private set; }
        public Fixture(int successfulWrites, int error)
        {
            Directory.CreateDirectory(_root);
            _log = new AppLog(Path.Combine(_root, "logs"));
            var path = Path.Combine(_root, "fake-hid.bin");
            Stream = new ErrorStream(path, successfulWrites, error);
            var info = new HidInterface(path, 0x046D, 0xB366, 0xFF43, 0x0202, 20, 20, "Fixture keyboard");
            Connection = new HidConnection("disconnected-write-fixture", [(info, Stream)], _log);
            Device = new HostDevice(Connection, 0xFF, _log);
            typeof(HostDevice).GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Device, Device.Info with { Name = "Keyboard", Kind = "키보드", FeatureIndex = 9 });
            Device.ReportLocalHost(new HostInfo(3, 0));
            Connection.Closed += _ => { Closures++; Device.MarkLinkClosed(); };
        }
        public Task<SwitchOutcome> SwitchAsync() => new SwitchCoordinator(_ => { }, verificationDelay: TimeSpan.Zero,
            retryDelay: TimeSpan.Zero).SwitchAsync(Device, 1, "fixture");
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            _log.Dispose();
            var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EasySwitch.Tests")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(_root);
            if (!target.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its root");
            Directory.Delete(target, true);
        }
    }

    private sealed class ErrorStream(string path, int successfulWrites, int error)
        : FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
        public int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ++Writes <= successfulWrites ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("Fixture I/O error", error));
    }
}
