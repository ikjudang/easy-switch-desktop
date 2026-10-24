using System.Text;
using System.IO;
using EasySwitch.Hidpp;
using EasySwitch.Probe;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class PersistenceAndTransportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EasySwitch.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LogIsFlushedBeforeShutdownAndHasMillisecondsAndDate()
    {
        using var log = new AppLog(_directory);
        log.Write("테스트 전환");
        var content = ReadShared(log.FilePath);
        Assert.Contains("테스트 전환", content);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}", content);
    }

    [Fact]
    public void ExportContainsExistingRecentEntriesAndSubsequentEntries()
    {
        using var log = new AppLog(Path.Combine(_directory, "local"));
        log.Write("before export");
        var export = Path.Combine(_directory, "export");
        log.SetExportDirectory(export);
        log.Write("after export");
        var content = ReadShared(Directory.GetFiles(export).Single());
        Assert.Contains("before export", content);
        Assert.Contains("after export", content);
    }

    [Fact]
    public void ReopeningSameExportDoesNotDuplicateHistory()
    {
        using var log = new AppLog(Path.Combine(_directory, "local"));
        var export = Path.Combine(_directory, "export");
        log.Write("once");
        log.SetExportDirectory(export);
        log.SetExportDirectory(export);
        Assert.Single(ReadShared(Directory.GetFiles(export).Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void InvalidExportPreservesExistingWriter()
    {
        using var log = new AppLog(Path.Combine(_directory, "local"));
        var export = Path.Combine(_directory, "export");
        log.SetExportDirectory(export);
        Assert.Throws<IOException>(() => log.SetExportDirectory(log.FilePath));
        log.Write("still exporting");
        Assert.Contains("still exporting", ReadShared(Directory.GetFiles(export).Single()));
    }

    [Fact]
    public void LogMemoryLimitDoesNotRemoveFileEvidence()
    {
        using var log = new AppLog(_directory);
        for (var i = 0; i < 702; i++) log.Write($"entry {i}");
        Assert.Equal(600, log.Recent().Count);
        Assert.Equal(702, ReadShared(log.FilePath).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("entry 0", ReadShared(log.FilePath));
    }

    [Fact]
    public void DeviceSelectionAndFollowStateSurviveRestart()
    {
        var settings = new SettingsStore(_directory);
        Assert.False(settings.Value.FollowEnabled);
        settings.Save(new AppSettings { KeyboardId = "keyboard", MouseId = "mouse", FollowEnabled = true, BounceEnabled = false });
        var reopened = new SettingsStore(_directory);
        Assert.Equal("keyboard", reopened.Value.KeyboardId);
        Assert.Equal("mouse", reopened.Value.MouseId);
        Assert.True(reopened.Value.FollowEnabled);
        Assert.False(reopened.Value.BounceEnabled);
        Assert.False(File.Exists(Path.Combine(_directory, "settings.json.tmp")));
    }

    [Fact]
    public void LegacySettingsDefaultToNormalAnimationSpeed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory,"settings.json"),"{\"FloatingSize\":150}");
        var settings=new SettingsStore(_directory);
        Assert.Equal(100,settings.Value.FloatingAnimationSpeed);Assert.Equal(150,settings.Value.FloatingSize);
    }
    [Theory]
    [InlineData(0,25)] [InlineData(25,25)] [InlineData(175,175)] [InlineData(300,300)] [InlineData(999,300)]
    public void AnimationSpeedIsClampedAndPersists(int requested,int expected)
    {
        var settings=new SettingsStore(_directory);settings.Save(new AppSettings{FloatingAnimationSpeed=requested});
        Assert.Equal(expected,settings.Value.FloatingAnimationSpeed);
        Assert.Equal(expected,new SettingsStore(_directory).Value.FloatingAnimationSpeed);
    }
    [Fact]
    public void MalformedSettingsAreReportedAndOriginalEvidenceRemains()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "malformed");
        var settings = new SettingsStore(_directory);
        Assert.NotNull(settings.LoadError);
        Assert.False(settings.Value.FollowEnabled);
        Assert.Equal("malformed", File.ReadAllText(path));
    }

    [Fact]
    public void ReceiverCollectionsGroupTogetherButDifferentReceiversStaySeparate()
    {
        var shortInfo = Info(@"\\?\hid#vid_046d&pid_c548&mi_02&col01#9&123&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 7);
        var longInfo = Info(@"\\?\hid#vid_046d&pid_c548&mi_02&col02#9&123&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", 20);
        var other = longInfo with { Path = longInfo.Path.Replace("9&123", "9&456") };
        Assert.Equal(HidConnection.GroupKey(shortInfo), HidConnection.GroupKey(longInfo));
        Assert.NotEqual(HidConnection.GroupKey(shortInfo), HidConnection.GroupKey(other));
    }

    [Fact]
    public async Task DirectBluetoothPadsShortRequestsToLongReports()
    {
        using var log = new AppLog(Path.Combine(_directory, "logs"));
        var path = Path.Combine(_directory, "long.bin");
        var stream = Open(path);
        await using (var connection = new HidConnection("test", [(Info(path, 20), stream)], log))
        {
            await connection.SendAsync(RootFeature.BuildGetFeature(0x1814).ToBytes(), CancellationToken.None);
        }
        var payload = File.ReadAllBytes(path);
        Assert.Equal(20, payload.Length);
        Assert.Equal(0x11, payload[0]);
        Assert.Equal(0xFF, payload[1]);
        Assert.Equal(new byte[] { 0x18, 0x14 }, payload[4..6]);
    }

    [Fact]
    public async Task ReceiverUsesShortWriterForShortReport()
    {
        using var log = new AppLog(Path.Combine(_directory, "logs"));
        var shortPath = Path.Combine(_directory, "short.bin");
        var longPath = Path.Combine(_directory, "long.bin");
        await using (var connection = new HidConnection("test", [(Info(shortPath, 7), Open(shortPath)), (Info(longPath, 20), Open(longPath))], log))
        {
            await connection.SendAsync(RootFeature.BuildGetFeature(0x1814, deviceIndex: 2).ToBytes(), CancellationToken.None);
        }
        Assert.Equal(7, new FileInfo(shortPath).Length);
        Assert.Equal(0, new FileInfo(longPath).Length);
        Assert.Equal(2, File.ReadAllBytes(shortPath)[1]);
    }

    private static HidInterface Info(string path, int length) => new(path, 0x046D, 0xC548, 0xFF00, length == 7 ? 1 : 2, length, length, "Test HID");
    private static FileStream Open(string path) => new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 0, FileOptions.Asynchronous);
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    public void Dispose()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EasySwitch.Tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its temporary root");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
