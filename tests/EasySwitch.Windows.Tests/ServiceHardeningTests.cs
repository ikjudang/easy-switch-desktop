using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows.Tests;

// Service, IPC, installation, persistence and logging behaviour that keeps the follower alive.
// No test installs a service, touches Program Files/ProgramData, or opens HID devices.
public sealed class ServiceHardeningTests
{
    private static readonly string Owner=WindowsIdentity.GetCurrent().User!.Value;
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;

    // S1 ------------------------------------------------------------------------------------------
    [Fact]
    public async Task ClientConnectsWhenTheServicePipeAppearsAfterTheFirstConnectTimeout()
    {
        var root=Temp();var name=PipeName();
        using var log=new AppLog(Path.Combine(root,"ui"));
        await using var client=new ServiceClient(log,name,Environment.ProcessId);
        client.Start();
        await Task.Delay(1600);// longer than one 1 s connect attempt
        Assert.False(client.Connected);
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        await Until(()=>client.Connected);
        Assert.Contains(log.Recent(),e=>e.Message.Contains("서비스 연결 재시도"));
    }
    [Fact]
    public async Task ClientKeepsTheConnectionWhenASnapshotSubscriberThrows()
    {
        var root=Temp();var name=PipeName();
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(root,"ui"));
        await using var client=new ServiceClient(log,name,Environment.ProcessId);
        var calls=0;client.SnapshotReceived+=_=>{if(Interlocked.Increment(ref calls)==1)throw new InvalidOperationException("fixture subscriber fault");};
        client.Start();
        await Until(()=>Volatile.Read(ref calls)>3);
        Assert.True(client.Connected);
        Assert.Contains(log.Recent(),e=>e.Message.Contains("fixture subscriber fault"));
    }
    [Fact]
    public async Task ClientFinishesTheInitialComparisonWhenTheSelectionBackupFails()
    {
        var root=Temp();var name=PipeName();
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        using var log=new AppLog(Path.Combine(root,"ui"));
        await using var client=new ServiceClient(log,name,Environment.ProcessId);
        client.RestorePreferences(EnginePreferences.From(new AppSettings{FollowEnabled=true,SelectedDeviceIds=["k"],SelectedDeviceNames=[new("k","Keyboard","키보드")],EngineSettingsRevision=5}));
        client.InitialPreferencesCompared+=_=>throw new IOException("fixture backup failure");
        var snapshots=0;client.SnapshotReceived+=_=>Interlocked.Increment(ref snapshots);client.Start();
        await Until(()=>Volatile.Read(ref snapshots)>3);
        Assert.True(client.Connected);
        Assert.Contains(log.Recent(),e=>e.Message.Contains("fixture backup failure"));
        await client.FlushConfigurationAsync();// the restored selection still reaches the service
        Assert.True((await RequestState(name)).State!.Preferences.FollowEnabled);
    }
    [Fact]
    public void ClientRetryDelayIsBounded()
    {
        Assert.Equal(500,ServiceClient.RetryDelay(1));
        Assert.All(Enumerable.Range(2,50),n=>Assert.Equal(1000,ServiceClient.RetryDelay(n)));
    }

    // S3 ------------------------------------------------------------------------------------------
    [Fact]
    public async Task DispatcherCallbackFailureIsLoggedAndTheServiceKeepsAnswering()
    {
        var root=Temp();var name=PipeName();
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        var exits=new ConcurrentQueue<int>();runtime.FatalExit=exits.Enqueue;
        _=Dispatcher(runtime).BeginInvoke(new Action(()=>throw new InvalidOperationException("fixture dispatcher fault")));
        var reply=await RequestState(name);
        Assert.Null(reply.Error);
        Assert.Contains("fixture dispatcher fault",ReadShared(EngineLog(runtime).FilePath));
        Assert.Empty(exits);
    }
    [Fact]
    public async Task DeviceThreadEndingWithoutAStopRequestExitsWithAFailureCode()
    {
        var root=Temp();var name=PipeName();
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        var exits=new ConcurrentQueue<int>();runtime.FatalExit=exits.Enqueue;
        var path=EngineLog(runtime).FilePath;
        Dispatcher(runtime).BeginInvokeShutdown(DispatcherPriority.Normal);
        await Until(()=>!exits.IsEmpty);
        Assert.Equal(1,exits.Single());
        Assert.Contains("종료 요청 없이",ReadShared(path));
    }
    [Fact]
    public async Task ABusyDeviceThreadReleasesThePipeInsteadOfHoldingItForever()
    {
        var root=Temp();var name=PipeName();
        await using var runtime=new BackgroundRuntime(root,name,Owner,true){DispatchLimit=TimeSpan.FromMilliseconds(300)};await runtime.StartAsync();
        using var release=new ManualResetEventSlim();
        _=Dispatcher(runtime).BeginInvoke(new Action(()=>release.Wait(10000)));
        var timer=Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<IOException>(()=>RequestState(name,1500));
        Assert.True(timer.ElapsedMilliseconds<1500,$"closed after {timer.ElapsedMilliseconds} ms");
        await Until(()=>ReadShared(EngineLog(runtime).FilePath).Contains("서비스 요청 처리 지연"));
        release.Set();
        Assert.Null((await RequestState(name)).Error);// the device thread answers again after the block
    }
    [Fact]
    public void LocalLogWriteFailureIsCountedAndShownInsteadOfThrown()
    {
        var dir=Temp();using var log=new AppLog(dir);
        var sink=typeof(AppLog).GetField("_local",Hidden)!.GetValue(log)!;
        var writer=(StreamWriter)sink.GetType().GetProperty("Writer")!.GetValue(sink)!;
        writer.BaseStream.Dispose();// stands in for a disk that rejects writes
        log.Write("after the disk failed");log.Write("second line");
        Assert.Equal(2,log.LocalWriteFailures);
        Assert.Contains(log.Recent(),e=>e.Message=="after the disk failed");
        Assert.Single(log.Recent(),e=>e.Message.StartsWith("로컬 로그 파일 기록 실패",StringComparison.Ordinal));
    }

    // S6 ------------------------------------------------------------------------------------------
    [Fact]
    public async Task LogRollsIntoPartsAndTheSessionReadReturnsEveryLineOnce()
    {
        var dir=Temp();using var log=new AppLog(dir,4096,long.MaxValue,TimeSpan.MaxValue);
        var first=log.FilePath;
        for(var i=0;i<400;i++){log.Write($"line {i:D4} "+new string('x',40));if(i%50==0)await Task.Delay(20);}
        await Until(()=>Rolling(log)==0);
        Assert.NotEqual(first,log.FilePath);Assert.Matches(@"-p\d+\.txt$",log.FilePath);
        var all=Encoding.UTF8.GetString(log.ReadSession(false,0,int.MaxValue).Data);
        for(var i=0;i<400;i++)Assert.Single(System.Text.RegularExpressions.Regex.Matches(all,$"line {i:D4} "));
        Assert.Contains("로그 파일 이어짐",all);
        Assert.True(all.IndexOf("line 0000",StringComparison.Ordinal)<all.IndexOf("line 0399",StringComparison.Ordinal));
    }
    [Fact]
    public async Task CodeExportSpansPartsWithASingleByteOrderMark()
    {
        var dir=Temp();using var log=new AppLog(dir,2048,long.MaxValue,TimeSpan.MaxValue);
        for(var i=0;i<300;i++){log.WriteCode($"ES1 R{i:D4} END E{i}");if(i%50==0)await Task.Delay(20);}
        await Until(()=>Rolling(log)==0);
        Assert.Matches(@"-p\d+\.codes\.txt$",log.CodeFilePath);
        var export=Path.Combine(Temp(),"codes.txt");log.ExportCodes(export);
        var bytes=File.ReadAllBytes(export);
        Assert.Equal(new byte[]{0xEF,0xBB,0xBF},bytes[..3]);
        Assert.Equal(-1,bytes.AsSpan(3).IndexOf(new byte[]{0xEF,0xBB,0xBF}));
        var text=Encoding.UTF8.GetString(bytes);
        for(var i=0;i<300;i++)Assert.Contains($"|R{i:D4}|",text);
    }
    [Fact]
    public void RetentionRemovesOldAndExcessLogsButKeepsCurrentForeignAndLockedFiles()
    {
        var dir=Temp();var now=DateTime.UtcNow;
        string Make(string name,int bytes,double ageDays){var path=Path.Combine(dir,name);File.WriteAllBytes(path,new byte[bytes]);File.SetLastWriteTimeUtc(path,now.AddDays(-ageDays));return path;}
        var old=Make("easyswitch-pc-old.txt",100,40);var oldCodes=Make("easyswitch-pc-old.codes.txt",100,40);
        var current=Make("easyswitch-pc-current.txt",100,45);
        var foreign=Make("notes.txt",100,90);
        var locked=Make("easyswitch-pc-locked.txt",100,50);
        var recent=Enumerable.Range(0,5).Select(i=>Make($"easyswitch-pc-r{i}.txt",1000,5-i)).ToArray();// r0 oldest
        using(new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var result=AppLog.ApplyRetention(dir,[current],now,TimeSpan.FromDays(30),2600);
            Assert.Equal(1,result.Skipped);
        }
        Assert.False(File.Exists(old));Assert.False(File.Exists(oldCodes));
        Assert.True(File.Exists(current));Assert.True(File.Exists(foreign));Assert.True(File.Exists(locked));
        // Size cap: remaining own files (current 100 + locked 100 + recent) must fit 2600 bytes, oldest removed first.
        Assert.False(File.Exists(recent[0]));Assert.False(File.Exists(recent[1]));Assert.False(File.Exists(recent[2]));
        Assert.True(File.Exists(recent[3]));Assert.True(File.Exists(recent[4]));
    }
    [Fact]
    public async Task StartupRetentionRunsInTheBackgroundAndLogsWhatItRemoved()
    {
        var dir=Temp();var stale=Path.Combine(dir,"easyswitch-pc-20200101-000000-1.txt");File.WriteAllText(stale,"old");File.SetLastWriteTimeUtc(stale,DateTime.UtcNow.AddDays(-60));
        using var log=new AppLog(dir);await log.Maintenance;
        Assert.False(File.Exists(stale));
        Assert.Contains(log.Recent(),e=>e.Message.StartsWith("로그 보존 정리",StringComparison.Ordinal));
    }
    [Fact]
    public void HistoryImportIsShownWithoutBeingWrittenAgain()
    {
        var dir=Temp();using var log=new AppLog(dir);
        var at=DateTimeOffset.Now.AddMinutes(-5);
        log.ImportHistory(new LogEntry(at,"already recorded by an earlier UI"));log.ImportHistory(new CodeLogEntry(at,"ES1|R0001|END"));
        log.Import(new LogEntry(at,"new to this UI"));
        Assert.Contains(log.Recent(),e=>e.Message=="already recorded by an earlier UI");Assert.Single(log.RecentCodes());
        var file=ReadShared(log.FilePath);
        Assert.DoesNotContain("already recorded by an earlier UI",file);Assert.Contains("new to this UI",file);
        Assert.DoesNotContain("R0001",ReadShared(log.CodeFilePath));
    }
    [Fact]
    public async Task ASecondUiLearnsWhichServiceEventsTheFirstUiAlreadyRecorded()
    {
        var root=Temp();var name=PipeName();var cursor=Path.Combine(root,"cursor.json");
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        EngineLog(runtime).Write("before the first UI");
        using var ui=new AppLog(Path.Combine(root,"ui"));
        await using(var first=new ServiceClient(ui,name,Environment.ProcessId){CursorFile=cursor})
        {
            var seen=new ConcurrentQueue<ServiceSnapshot>();first.SnapshotReceived+=seen.Enqueue;first.Start();
            await Until(()=>seen.Any(s=>s.Events.Any(e=>e.Log?.Message=="before the first UI")));
            Assert.All(seen,s=>Assert.Equal(0,s.ClientRecordedThrough));
        }
        Assert.True(File.Exists(cursor));
        EngineLog(runtime).Write("after the first UI");
        await using var second=new ServiceClient(ui,name,Environment.ProcessId){CursorFile=cursor};
        var snapshots=new ConcurrentQueue<ServiceSnapshot>();second.SnapshotReceived+=snapshots.Enqueue;second.Start();
        await Until(()=>snapshots.Any(s=>s.Events.Any(e=>e.Log?.Message=="after the first UI")));
        var events=snapshots.SelectMany(s=>s.Events.Select(e=>(Event:e,Through:s.ClientRecordedThrough))).ToArray();
        var earlier=events.Single(x=>x.Event.Log?.Message=="before the first UI");Assert.True(earlier.Event.Sequence<=earlier.Through);
        var later=events.Single(x=>x.Event.Log?.Message=="after the first UI");Assert.True(later.Event.Sequence>later.Through);
    }

    // S7 ------------------------------------------------------------------------------------------
    [Fact]
    public void SettingsKeepTheLastGoodSaveAndPreserveAnUnreadableFile()
    {
        var dir=Temp();var store=new SettingsStore(dir);
        store.Save(new AppSettings{FollowEnabled=true,KeyboardId="first"});
        store.Save(new AppSettings{FollowEnabled=true,KeyboardId="second"});
        store.Save(new AppSettings{FollowEnabled=true,KeyboardId="third"});
        var main=Path.Combine(dir,"settings.json");
        Assert.Contains("third",File.ReadAllText(main));Assert.Contains("second",File.ReadAllText(main+".bak"));
        Assert.False(File.Exists(main+".tmp"));
        File.WriteAllBytes(main,new byte[64]);// a write cut off by power loss
        var recovered=new SettingsStore(dir);
        Assert.True(recovered.RestoredFromBackup);Assert.NotNull(recovered.LoadError);
        Assert.True(recovered.Value.FollowEnabled);Assert.Equal("second",recovered.Value.KeyboardId);
        recovered.Save(recovered.Value with{KeyboardId="fourth"});
        Assert.Contains("fourth",File.ReadAllText(main));
        Assert.Contains("second",File.ReadAllText(main+".bak"));// the good backup is not replaced by the broken file
        Assert.Equal(new byte[64],File.ReadAllBytes(Directory.GetFiles(dir,"settings.json.unreadable-*").Single()));
    }
    [Fact]
    public void MissingSettingsWithABackupRestoreTheBackup()
    {
        var dir=Temp();
        File.WriteAllText(Path.Combine(dir,"settings.json.bak"),JsonSerializer.Serialize(new AppSettings{FollowEnabled=true}));
        var store=new SettingsStore(dir);
        Assert.True(store.RestoredFromBackup);Assert.True(store.Value.FollowEnabled);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ServiceLogsUnreadableSettingsAndWhichValuesItUses(bool backup)
    {
        var root=Temp();var name=PipeName();var engine=Path.Combine(root,"engine");Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(engine,"settings.json"),"{");
        if(backup)File.WriteAllText(Path.Combine(engine,"settings.json.bak"),JsonSerializer.Serialize(new AppSettings{FollowEnabled=true}));
        await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
        Assert.Contains("서비스 설정 파일을 읽지 못함",ReadShared(EngineLog(runtime).FilePath));
        Assert.Contains($"SVC|SETTINGS|{(backup?"BACKUP":"DEFAULT")}",ReadShared(EngineLog(runtime).CodeFilePath));
        Assert.Equal(backup,(await RequestState(name)).State!.Preferences.FollowEnabled);
    }

    // S8 ------------------------------------------------------------------------------------------
    [Fact]
    public async Task ServiceRefusesAPipeNameAnotherProcessCreatedFirstAndRecoversWhenItIsFreed()
    {
        var root=Temp();var name=PipeName();
        var squatter=new NamedPipeServerStream(name,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        try
        {
            await using var runtime=new BackgroundRuntime(root,name,Owner,true);await runtime.StartAsync();
            await Until(()=>ReadShared(EngineLog(runtime).FilePath).Contains("서비스 통신 통로 준비 실패"));
            squatter.Dispose();
            Assert.Null((await RequestState(name,15000)).Error);
            Assert.Contains("서비스 통신 통로 복구",ReadShared(EngineLog(runtime).FilePath));
        }
        finally{squatter.Dispose();}
    }
    [Fact]
    public void PipeRetryBacksOffOnlyAfterRepeatedFailures()
    {
        Assert.Equal(0,BackgroundRuntime.PipeRetryDelay(1));
        Assert.Equal(100,BackgroundRuntime.PipeRetryDelay(2));Assert.Equal(200,BackgroundRuntime.PipeRetryDelay(3));
        Assert.Equal(5000,BackgroundRuntime.PipeRetryDelay(20));Assert.Equal(5000,BackgroundRuntime.PipeRetryDelay(int.MaxValue));
    }

    // S2 ------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\EasySwitch.Service.exe\" --service",true)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\easyswitch.service.EXE\" --service",true)]
    [InlineData("C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\EasySwitch.Service.exe --service",false)]
    [InlineData("\"C:\\Users\\Public\\EasySwitch.Service.exe\" --service",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\..\\..\\Evil\\0.2.40-0123456789AB\\EasySwitch.Service.exe\"",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\ServiceX\\0.2.40-0123456789AB\\EasySwitch.Service.exe\"",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\other.exe\"",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\sub\\EasySwitch.Service.exe\"",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\EasySwitch.Service.exe\"",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789ab\\EasySwitch.Service.exe\"",false)]
    [InlineData("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\EasySwitch.Service.exe:payload\"",false)]
    [InlineData("\"EasySwitch.Service.exe\" --service",false)]
    [InlineData("\"\\\\server\\share\\EasySwitch\\Service\\0.2.40-0123456789AB\\EasySwitch.Service.exe\"",false)]
    [InlineData("",false)]
    [InlineData(null,false)]
    public void RollbackOnlyReturnsAnExistingServiceCopyUnderProgramFiles(string? imagePath,bool accepted)
    {
        var result=ServiceInstallTrust.RollbackExecutable(imagePath,@"C:\Program Files",_=>true);
        if(accepted)Assert.Equal(@"C:\Program Files\EasySwitch\Service\0.2.40-0123456789AB\"+Path.GetFileName(ServiceInstallTrust.ExecutableFromImagePath(imagePath)!),result,ignoreCase:true);
        else Assert.Null(result);
    }
    [Fact]
    public void RollbackRejectsACopyThatNoLongerExists()
        =>Assert.Null(ServiceInstallTrust.RollbackExecutable("\"C:\\Program Files\\EasySwitch\\Service\\0.2.40-0123456789AB\\EasySwitch.Service.exe\" --service",@"C:\Program Files",_=>false));
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DataFolderRepairRefusesALinkBeforeChangingAnything(bool parentIsLink)
    {
        var temp=Temp();var target=Path.Combine(temp,"elsewhere");Directory.CreateDirectory(target);
        var before=Sddl(target);
        string root;
        if(parentIsLink){var parent=Path.Combine(temp,"EasySwitch");Junction(parent,target);root=Path.Combine(parent,"Service");}
        else{var parent=Path.Combine(temp,"EasySwitch");Directory.CreateDirectory(parent);root=Path.Combine(parent,"Service");Junction(root,target);}
        Assert.Throws<IOException>(()=>ServiceInstallTrust.SecureDataRoot(root,Owner));
        Assert.Equal(before,Sddl(target));Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
    [Fact]
    public void DataFolderResetRemovesLinksWithoutFollowingThem()
    {
        var root=Temp();var target=Temp();File.WriteAllText(Path.Combine(target,"secret.txt"),"keep");
        File.WriteAllText(Path.Combine(root,"installation.json"),"x");Directory.CreateDirectory(Path.Combine(root,"engine"));File.WriteAllText(Path.Combine(root,"engine","settings.json"),"x");
        Junction(Path.Combine(root,"engine","link"),target);
        var visited=new List<string>();var notes=new List<string>();
        var counts=ServiceInstallTrust.ResetEntries(new DirectoryInfo(root),entry=>visited.Add(Path.GetRelativePath(root,entry.FullName)),notes);
        Assert.Equal(new[]{"engine",@"engine\settings.json","installation.json"},visited.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal((3,1,0),counts);
        Assert.False(Directory.Exists(Path.Combine(root,"engine","link")));
        Assert.Equal("keep",File.ReadAllText(Path.Combine(target,"secret.txt")));
    }
    [Fact]
    public void OnlySystemAndAdministratorsAreTrustedDataOwners()
    {
        Assert.True(ServiceInstallTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)));
        Assert.True(ServiceInstallTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)));
        Assert.False(ServiceInstallTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null)));
        Assert.False(ServiceInstallTrust.IsTrustedOwner(new SecurityIdentifier(Owner)));
        Assert.False(ServiceInstallTrust.IsTrustedOwner(null));
    }

    // S4 ------------------------------------------------------------------------------------------
    [Fact]
    public void ServiceCopyContainsOnlyTheAppsOwnFiles()
    {
        var app=FakeApp();
        var plan=ServicePayload.Plan(app,"EasySwitch").Select(p=>p.Replace('\\','/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(new[]
        {
            "Assets/Licenses/OFL.txt","coreclr.dll","EasySwitch.deps.json","EasySwitch.dll","EasySwitch.exe","EasySwitch.runtimeconfig.json",
            "ko/EasySwitch.resources.dll","ko/System.Runtime.resources.dll","Pkg.dll","System.Runtime.dll",
        }.Order(StringComparer.OrdinalIgnoreCase),plan);
    }
    [Fact]
    public void ServiceCopyIsRefusedWhenAListedFileIsMissing()
    {
        var app=FakeApp();File.Delete(Path.Combine(app,"coreclr.dll"));
        Assert.Throws<IOException>(()=>ServicePayload.Plan(app,"EasySwitch"));
    }
    [Fact]
    public void ServiceCopyVerifiesHashesAndRepairsAChangedFile()
    {
        var app=FakeApp();var target=Path.Combine(Temp(),"0.2.41-0123456789AB");var exe=Path.Combine(target,"EasySwitch.Service.exe");
        var expected=ServicePayload.Plan(app,"EasySwitch").Count+1;
        Assert.Equal(expected,ServicePayload.CopyVerified(Path.Combine(app,"EasySwitch.exe"),app,"EasySwitch",target,exe));
        Assert.Equal(0,ServicePayload.CopyVerified(Path.Combine(app,"EasySwitch.exe"),app,"EasySwitch",target,exe));
        File.WriteAllText(Path.Combine(target,"Pkg.dll"),"tampered");
        Assert.Equal(1,ServicePayload.CopyVerified(Path.Combine(app,"EasySwitch.exe"),app,"EasySwitch",target,exe));
        Assert.Equal(File.ReadAllBytes(Path.Combine(app,"Pkg.dll")),File.ReadAllBytes(Path.Combine(target,"Pkg.dll")));
        Assert.False(File.Exists(Path.Combine(target,"unrelated.pdf")));Assert.False(Directory.Exists(Path.Combine(target,"Movies")));
    }
    [Fact]
    public void OldServiceCopiesAreRemovedExceptCurrentRollbackRunningAndForeignFolders()
    {
        var root=Temp();
        string Copy(string name){var path=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(path,"ko"));File.WriteAllText(Path.Combine(path,"EasySwitch.dll"),"x");File.WriteAllText(Path.Combine(path,"ko","a.resources.dll"),"x");return path;}
        var stale=Copy("0.2.38-AAAAAAAAAAAA");var running=Copy("0.2.39-BBBBBBBBBBBB");var rollback=Copy("0.2.40-CCCCCCCCCCCC");var current=Copy("0.2.41-DDDDDDDDDDDD");
        var foreign=Path.Combine(root,"notes");Directory.CreateDirectory(foreign);
        File.Copy(Path.Combine(Environment.SystemDirectory,"ping.exe"),Path.Combine(running,"EasySwitch.Service.exe"));
        using var process=Process.Start(new ProcessStartInfo(Path.Combine(running,"EasySwitch.Service.exe")){ArgumentList={"-n","30","127.0.0.1"},UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})!;
        try
        {
            using var log=new AppLog(Path.Combine(Temp(),"logs"));
            var result=ServicePayload.RemoveOldCopies(root,[current,rollback],log);
            Assert.Equal((1,1,0),result);
            Assert.False(Directory.Exists(stale));
            Assert.True(File.Exists(Path.Combine(running,"EasySwitch.Service.exe")));Assert.True(File.Exists(Path.Combine(running,"EasySwitch.dll")));
            Assert.True(Directory.Exists(rollback));Assert.True(Directory.Exists(current));Assert.True(Directory.Exists(foreign));
            Assert.Contains(log.Recent(),e=>e.Message.Contains("실행 중이라 남김"));
        }
        finally{try{process.Kill();process.WaitForExit(5000);}catch(InvalidOperationException){}}
    }

    // S5 ------------------------------------------------------------------------------------------
    private static BootServiceDecision Decide(bool force=false,bool optedOut=false,string? declined=null,string? installedVersion="0.2.41",string? owner=null,
        bool registered=true,ServiceControllerStatus? status=ServiceControllerStatus.Running,bool failed=false,bool sourceRun=false,bool waited=false)
        =>BootServicePolicy.Decide(force,optedOut,declined,installedVersion is null?null:new ServiceInstallation(installedVersion,owner??Owner,"x"),
            new(registered,status,failed),Owner,new Version(0,2,41),"0.2.41",sourceRun,waited);
    [Fact]
    public void BootServicePromptsOnlyWhenApprovalCanHelp()
    {
        Assert.Equal(BootServiceAction.Skip,Decide(status:ServiceControllerStatus.Running).Action);
        Assert.Equal(BootServiceAction.Skip,Decide(optedOut:true,status:null,registered:false,installedVersion:null).Action);
        Assert.Equal(BootServiceAction.Prompt,Decide(force:true,optedOut:true,status:null,registered:false,installedVersion:null).Action);
        Assert.Equal(BootServiceAction.Skip,Decide(installedVersion:"0.2.50",status:ServiceControllerStatus.Stopped).Action);
        Assert.Equal(BootServiceAction.Skip,Decide(installedVersion:"0.2.40",owner:"S-1-5-21-1-2-3-4",status:ServiceControllerStatus.Running).Action);
        Assert.Equal(BootServiceAction.Skip,Decide(registered:false,installedVersion:null,status:null,sourceRun:true).Action);
        Assert.Equal(BootServiceAction.Prompt,Decide(registered:false,installedVersion:null,status:null).Action);
        Assert.Equal(BootServiceAction.Prompt,Decide(installedVersion:"0.2.40",status:ServiceControllerStatus.Running).Action);
    }
    [Theory]
    [InlineData(ServiceControllerStatus.StartPending)] [InlineData(ServiceControllerStatus.ContinuePending)] [InlineData(ServiceControllerStatus.StopPending)]
    public void AStartingServiceIsAwaitedBeforeAnyPrompt(ServiceControllerStatus status)
    {
        var first=Decide(status:status);
        Assert.Equal(BootServiceAction.Wait,first.Action);Assert.Equal(BootServicePolicy.StartWait,first.Wait);
        Assert.Equal(BootServiceAction.Prompt,Decide(status:status,waited:true).Action);
    }
    [Fact]
    public void ACrashedServiceIsGivenTimeForTheWindowsRestartBeforeAnyPrompt()
    {
        var first=Decide(status:ServiceControllerStatus.Stopped,failed:true);
        Assert.Equal(BootServiceAction.Wait,first.Action);Assert.Equal(BootServicePolicy.RestartWait,first.Wait);
        Assert.Equal(BootServiceAction.Prompt,Decide(status:ServiceControllerStatus.Stopped,failed:true,waited:true).Action);
    }
    [Fact]
    public void AStoppedServiceStillPromptsBecauseNothingFollowsWithoutIt()
    {
        var decision=Decide(status:ServiceControllerStatus.Stopped);
        Assert.Equal(BootServiceAction.Prompt,decision.Action);Assert.Contains("키보드 따라 전환이 멈춰",decision.Reason);
    }
    [Fact]
    public void ACancelledApprovalIsNotAskedAgainForTheSameVersionUnlessTheUserAsks()
    {
        Assert.Equal(BootServiceAction.Skip,Decide(declined:"0.2.41",status:ServiceControllerStatus.Stopped).Action);
        Assert.Equal(BootServiceAction.Skip,Decide(declined:"0.2.41",registered:false,installedVersion:null,status:null).Action);
        Assert.Equal(BootServiceAction.Prompt,Decide(declined:"0.2.40",registered:false,installedVersion:null,status:null).Action);
        Assert.Equal(BootServiceAction.Prompt,Decide(force:true,declined:"0.2.41",status:ServiceControllerStatus.Stopped).Action);
        Assert.Equal(BootServiceAction.Skip,Decide(declined:"0.2.41",status:ServiceControllerStatus.Running).Action);
    }

    // S12 -----------------------------------------------------------------------------------------
    [Fact]
    public void CrashRecordsAreAppendedWithMillisecondTimestamps()
    {
        var file=Path.Combine(Temp(),"crash.txt");
        CrashRecorder.Install(file);
        CrashRecorder.Record("fixture failure","first");CrashRecorder.Record("fixture failure","second");
        var lines=File.ReadAllLines(file);
        Assert.Equal(2,lines.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[\d+\] fixture failure: first$",lines[0]);
        Assert.EndsWith("fixture failure: second",lines[1]);
    }

    // helpers -------------------------------------------------------------------------------------
    private static string FakeApp()
    {
        var app=Temp();
        void File_(string relative,string content="x"){var path=Path.Combine(app,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,content+relative);}
        File.WriteAllText(Path.Combine(app,"EasySwitch.deps.json"),"""
        {"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"},
         "targets":{".NETCoreApp,Version=v10.0/win-x64":{
           "EasySwitch/0.2.41":{"runtime":{"EasySwitch.dll":{}},"resources":{"ko/EasySwitch.resources.dll":{"locale":"ko"}}},
           "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0":{"runtime":{"System.Runtime.dll":{}},"native":{"coreclr.dll":{}}},
           "Pkg/1.0.0":{"runtime":{"runtimes/win/lib/net10.0/Pkg.dll":{}},"runtimeTargets":{"runtimes/unix/lib/net10.0/Pkg.dll":{"rid":"unix","assetType":"runtime"}}}}}}
        """);
        foreach(var file in new[]{"EasySwitch.dll","EasySwitch.exe","EasySwitch.runtimeconfig.json","System.Runtime.dll","coreclr.dll","Pkg.dll",
            @"ko\EasySwitch.resources.dll",@"ko\System.Runtime.resources.dll",@"ko\Other.resources.dll",@"Assets\Licenses\OFL.txt",@"Assets\Fonts\x.ttf",
            "README.txt","unrelated.pdf",@"Movies\a.mp4",@"Other\EasySwitch.dll"})File_(file);
        return app;
    }
    private static int Rolling(AppLog log)=>(int)typeof(AppLog).GetField("_rolling",Hidden)!.GetValue(log)!;
    private static Dispatcher Dispatcher(BackgroundRuntime runtime)=>(Dispatcher)typeof(BackgroundRuntime).GetField("_dispatcher",Hidden)!.GetValue(runtime)!;
    private static AppLog EngineLog(BackgroundRuntime runtime)=>(AppLog)typeof(BackgroundRuntime).GetField("_log",Hidden)!.GetValue(runtime)!;
    private static async Task<ServiceReply> RequestState(string name,int timeout=5000)
    {
        using var deadline=new CancellationTokenSource(timeout);
        await using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await ServiceWire.WriteAsync(pipe,new ServiceRequest(),deadline.Token);
        return await ServiceWire.ReadAsync<ServiceReply>(pipe,deadline.Token);
    }
    private static void Junction(string link,string target)
    {
        using var process=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")){ArgumentList={"/c","mklink","/J",link,target},UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        process.WaitForExit(10000);Assert.True(Directory.Exists(link) && (File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0,"junction fixture was not created");
    }
    private static string Sddl(string path)=>new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.All);
    private static string PipeName()=>"EasySwitch.Hardening."+Guid.NewGuid().ToString("N");
    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string ReadShared(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    private static async Task Until(Func<bool> condition)
    {var until=Environment.TickCount64+10000;while(!condition()){if(Environment.TickCount64>until)throw new System.TimeoutException("Hardening fixture timed out");await Task.Delay(20);}}
}
