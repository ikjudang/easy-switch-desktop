using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class UiWorkTests
{
    [Fact]
    public async Task DeferredSettingsStayResponsiveAndPersistOnlyTheLatestPendingSnapshot()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var writes=new ConcurrentQueue<AppSettings>();
        var store=new SettingsStore(Temp(),value=>{entered.Set();release.Wait(TimeSpan.FromSeconds(3));writes.Enqueue(value);});
        var timer=Stopwatch.StartNew();var first=store.SaveDeferred(new(){FloatingSize=50});Assert.True(timer.ElapsedMilliseconds<100);
        try
        {
            Assert.True(entered.Wait(2000));var second=store.SaveDeferred(store.Value with{FloatingSize=126});var third=store.SaveDeferred(store.Value with{FloatingSize=200});
            Assert.Equal(200,store.Value.FloatingSize);Assert.False(third.IsCompleted);release.Set();await Task.WhenAll(first,second,third);await store.FlushAsync();
            Assert.Equal(2,writes.Count);Assert.Equal(200,writes.Last().FloatingSize);
        }
        finally{release.Set();await store.FlushAsync();}
    }
    [Fact]
    public async Task SynchronousSettingsCallStillWaitsForDurableCompletion()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var store=new SettingsStore(Temp(),_=>{entered.Set();release.Wait(TimeSpan.FromSeconds(3));});
        var saving=Task.Run(()=>store.Save(new(){FollowEnabled=true}));
        try{Assert.True(entered.Wait(2000));Assert.False(saving.IsCompleted);}finally{release.Set();await saving;}
    }
    [Fact]
    public void FailedSynchronousSettingsSaveDoesNotPublishUncommittedState()
    {
        var store=new SettingsStore(Temp(),_=>throw new IOException("fixture"));var events=0;store.Saved+=_=>events++;
        Assert.Throws<IOException>(()=>store.Save(new(){FollowEnabled=true}));Assert.False(store.Value.FollowEnabled);Assert.Equal(0,events);
    }
    [Fact]
    public async Task DeferredLogsPreserveOriginalTimesAndFieldsWhenWriterIsBusy()
    {
        using var log=new AppLog(Temp());using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var blocked=log.QueueWork(()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(3));});Assert.True(entered.Wait(2000));log.EnableDeferredWrites();
        var original=DateTimeOffset.Now.AddMinutes(-2);var timer=Stopwatch.StartNew();
        try
        {
            log.Write("toggle enabled");log.WriteCode("ES1 UI ANIM F12 J0 DM16 UM123 NP0");log.Import(new LogEntry(original,"original service timestamp"));
            Assert.True(timer.ElapsedMilliseconds<100);Assert.Empty(log.Recent());release.Set();await blocked;await log.FlushAsync();
            Assert.Equal(original,log.Recent().First().At);Assert.Equal("ES1|UI|ANIM|F12|J0|DM16|UM123|NP0",log.RecentCodes().Single().Code);
            Assert.Contains("original service timestamp",Read(log.FilePath));Assert.Contains("F12|J0|DM16|UM123|NP0",Read(log.CodeFilePath));
        }
        finally{release.Set();}
    }
    [Fact]
    public async Task QueueFailureDoesNotStopFollowingLogDeliveries()
    {
        using var log=new AppLog(Temp());log.EnableDeferredWrites();await Assert.ThrowsAsync<IOException>(()=>log.QueueWork(()=>throw new IOException("fixture")));
        log.Post("still delivered");await log.FlushAsync();Assert.Contains("still delivered",Read(log.FilePath));
    }
    [Fact]
    public async Task DeferredSettingsReportFailuresAndAllowTheNextSave()
    {
        var failures=new ConcurrentQueue<Exception>();var attempts=0;
        var store=new SettingsStore(Temp(),_=>{if(Interlocked.Increment(ref attempts)==1)throw new IOException("fixture");});store.SaveFailed+=failures.Enqueue;
        await Assert.ThrowsAsync<IOException>(()=>store.SaveDeferred(new(){FloatingSize=50}));await store.SaveDeferred(new(){FloatingSize=126});
        Assert.Single(failures);Assert.Equal(126,store.Value.FloatingSize);
    }
    [Fact]
    public async Task NormalDeferredSaveUsesValidAtomicJsonAndFlushesLatestValue()
    {
        var path=Temp();var store=new SettingsStore(path);var writes=Enumerable.Range(1,60).Select(i=>store.SaveDeferred(new(){FloatingSize=i})).ToArray();
        await Task.WhenAll(writes);await store.FlushAsync();var persisted=JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(path,"settings.json")));
        Assert.Equal(60,persisted!.FloatingSize);Assert.False(File.Exists(Path.Combine(path,"settings.json.tmp")));
    }
    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string Read(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(stream);return reader.ReadToEnd();}
}
