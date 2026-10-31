using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Hardware;

namespace EasySwitch.Windows.Service;

internal sealed record ServiceInstallation(string Version,string OwnerSid,string Executable);
internal sealed record ServiceSetup(string OwnerSid,EnginePreferences Preferences);
internal static class ServiceRegistration
{
    public const string Name="EasySwitch.DeviceService";
    public const string PipeName="EasySwitch.DeviceService.v1";
    public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"EasySwitch","Service");
    public static string InstallationFile=>Path.Combine(Root,"installation.json");
    public static bool Installed{get{using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name);return key is not null;}}
    public static ServiceInstallation? Installation
    {
        get{try{return JsonSerializer.Deserialize<ServiceInstallation>(File.ReadAllText(InstallationFile));}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){return null;}}
    }
    public static string CurrentSid=>WindowsIdentity.GetCurrent().User!.Value;
    public static bool IsAdmin=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    /// <param name="first">The server has not created an instance yet: fail instead of joining a pipe another process already owns.</param>
    public static NamedPipeServerStream CreatePipe(string name,string owner,bool fixture=false,bool first=false)
    {
        var options=PipeOptions.Asynchronous|(first?PipeOptions.FirstPipeInstance:PipeOptions.None);
        if(fixture)return new(name,PipeDirection.InOut,4,PipeTransmissionMode.Byte,options|PipeOptions.CurrentUserOnly);
        var acl=new PipeSecurity();
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            acl.AddAccessRule(new(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(owner),PipeAccessRights.ReadWrite,AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,4,PipeTransmissionMode.Byte,options,65536,65536,acl);
    }
    public static void VerifyServer(NamedPipeClientStream pipe,int? fixturePid=null)
    {
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out var pid))throw new Win32Exception();
        if(fixturePid is {} expected){if(pid!=expected)throw new UnauthorizedAccessException("Fixture pipe owner mismatch");return;}
        using var scm=new NativeHandle(OpenSCManager(null,null,1));
        if(scm.Value==0)throw new Win32Exception();
        using var service=new NativeHandle(OpenService(scm.Value,Name,4));
        if(service.Value==0)throw new Win32Exception();
        if(!QueryServiceStatusEx(service.Value,0,out var status,Marshal.SizeOf<ServiceStatusProcess>(),out _))throw new Win32Exception();
        if(status.ProcessId!=pid || pid==0)throw new UnauthorizedAccessException("등록된 EasySwitch 서비스가 아닌 통신 상대");
    }
    public static async Task EnsureInstalledAsync(AppLog log,bool force=false)
    {
        bool optedOut;string? declined;
        using(var user=Registry.CurrentUser.OpenSubKey(@"Software\EasySwitch"))
        {
            optedOut=user?.GetValue("BootServiceOptOut") is int optOut && optOut==1;
            declined=user?.GetValue(BootServicePolicy.DeclinedVersionValue) as string;
        }
        var installed=Installation;
        var exe=Environment.ProcessPath!;
        var sourceRun=Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase);
        var decision=BootServicePolicy.Decide(force,optedOut,declined,installed,QueryState(),CurrentSid,AppVersion.Current,AppVersion.Label,sourceRun,false);
        if(decision.Action==BootServiceAction.Wait)
        {
            // A service that is starting (right after logon) or about to be restarted by Windows needs no approval.
            log.Write(decision.Reason);
            decision=BootServicePolicy.Decide(force,optedOut,declined,installed,await WaitForRunningAsync(decision.Wait),CurrentSid,AppVersion.Current,AppVersion.Label,sourceRun,true);
        }
        log.Write(decision.Reason);
        if(decision.Action!=BootServiceAction.Prompt)return;
        try
        {
            var setup=Path.Combine(SettingsStore.DataDirectory,"service-setup-"+Guid.NewGuid().ToString("N")+".json");
            File.WriteAllText(setup,JsonSerializer.Serialize(new ServiceSetup(CurrentSid,EnginePreferences.From(new SettingsStore().Value))));
            try
            {
            var start=new ProcessStartInfo(exe){UseShellExecute=true,Verb="runas"};start.ArgumentList.Add("--install-service");
            start.ArgumentList.Add("--service-setup");start.ArgumentList.Add(setup);
            using var process=Process.Start(start)??throw new IOException("서비스 설치 프로그램 시작 실패");
            await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new IOException($"서비스 설치 결과 {process.ExitCode}. {Path.Combine(Root,"install-result.txt")}");
            }
            finally{File.Delete(setup);}
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223)
        {
            // ERROR_CANCELLED: the user declined the approval prompt. Do not ask again on every start of this version.
            try{using var options=Registry.CurrentUser.CreateSubKey(@"Software\EasySwitch");options.SetValue(BootServicePolicy.DeclinedVersionValue,AppVersion.Label);}
            catch(Exception failure)when(failure is IOException or UnauthorizedAccessException or System.Security.SecurityException){log.Write("관리자 승인 취소 기록 실패: "+failure.Message);}
            log.Write($"로그인 전 실행 서비스 설치 미완료: 사용자가 관리자 승인을 취소함. {AppVersion.Label}에서는 자동으로 다시 묻지 않고 트레이의 '로그인 전 실행 설치·업데이트'로 다시 요청할 수 있음");
        }
        catch(Exception error)when(error is Win32Exception or IOException){log.Write($"로그인 전 실행 서비스 설치 미완료: {error.Message}");}
    }
    /// <summary>Service state readable without administrator rights, including whether the last stop was a failure.</summary>
    internal static BootServiceState QueryState()
    {
        if(!Installed)return new(false,null,false);
        using var scm=new NativeHandle(OpenSCManager(null,null,1));
        if(scm.Value==0)return new(true,null,false);
        using var service=new NativeHandle(OpenService(scm.Value,Name,4));
        if(service.Value==0 || !QueryServiceStatusEx(service.Value,0,out var status,Marshal.SizeOf<ServiceStatusProcess>(),out _))return new(true,null,false);
        // A clean stop reports exit code 0; a crash or failed start leaves a non-zero code and triggers recovery actions.
        return new(true,(ServiceControllerStatus)status.State,status.State==1 && status.Win32Exit!=0);
    }
    private static async Task<BootServiceState> WaitForRunningAsync(TimeSpan limit)
    {
        var until=Environment.TickCount64+(long)limit.TotalMilliseconds;
        BootServiceState state;
        do
        {
            await Task.Delay(250);state=QueryState();
            if(state.Status==ServiceControllerStatus.Running || state.Status==ServiceControllerStatus.Stopped && !state.StoppedByFailure)return state;
        }while(Environment.TickCount64<until);
        return state;
    }
    /// <summary>The service's current executable as registered with Windows (administrator-only data), read before it changes.</summary>
    internal static string? ReadImagePath()
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name);
        return key?.GetValue("ImagePath",null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    private sealed class NativeHandle(nint value):IDisposable{public nint Value=>value;public void Dispose(){if(value!=0)CloseServiceHandle(value);}}
    [StructLayout(LayoutKind.Sequential)]private struct ServiceStatusProcess{public uint Type,State,Accepted,Win32Exit,ServiceExit,Checkpoint,WaitHint,ProcessId,Flags;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(nint pipe,out uint pid);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint OpenService(nint scm,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(nint service,int level,out ServiceStatusProcess status,int size,out int needed);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(nint service);
}

internal static class ServiceInstaller
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile","IL3000",Justification="Assembly.Location intentionally distinguishes bundled and folder distributions.")]
    public static int Install(string? setupPath=null)
    {
        try
        {
            if(!ServiceRegistration.IsAdmin)throw new UnauthorizedAccessException("관리자 권한으로 서비스 설치가 필요해.");
            ServiceSetup? setup=null;
            if(setupPath is not null)
            {
                var sourceSetup=new FileInfo(Path.GetFullPath(setupPath));if(sourceSetup.Length>65536)throw new InvalidDataException("설치 설정이 너무 커.");
                setup=JsonSerializer.Deserialize<ServiceSetup>(File.ReadAllText(sourceSetup.FullName))??throw new InvalidDataException("설치 설정 없음");
                var fileOwner=sourceSetup.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier??throw new UnauthorizedAccessException("설정 파일 소유자를 확인할 수 없어.");
                if(fileOwner.Value!=setup.OwnerSid)throw new UnauthorizedAccessException("설정 파일 소유자와 설치할 Windows 계정이 달라.");
                _=new SecurityIdentifier(setup.OwnerSid);setup.Preferences.Validate();
            }
            var owner=setup?.OwnerSid??ServiceRegistration.CurrentSid;
            // Another account may have created the data folder before the first install; take it back before trusting its files.
            var securityNotes=ServiceInstallTrust.SecureDataRoot(ServiceRegistration.Root,owner);
            var old=ServiceRegistration.Installation;
            if(ServiceRegistration.Installed && old is null)throw new IOException("기존 서비스의 등록 정보를 확인할 수 없어 자동으로 교체하지 않았어.");
            if(Version.TryParse(old?.Version,out var oldVersion) && oldVersion>AppVersion.Current)throw new IOException("더 최신 EasySwitch 서비스가 이미 설치되어 있어.");
            if(old is not null && old.OwnerSid!=owner)throw new UnauthorizedAccessException("기존 서비스를 관리하는 Windows 계정에서 설치해 줘.");
            var source=Environment.ProcessPath!;
            var assemblyPath=typeof(ServiceInstaller).Assembly.Location;
            var payload=assemblyPath.Length>0?assemblyPath:source;
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(payload)))[..12];
            var programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var serviceRoot=Path.Combine(programFiles,"EasySwitch","Service");
            var targetRoot=Path.Combine(serviceRoot,AppVersion.Label+"-"+hash);
            Directory.CreateDirectory(targetRoot);
            var target=Path.Combine(targetRoot,ServiceInstallTrust.ServiceExecutable);
            // Copy and verify before the running service is stopped, so a bad copy never costs following time.
            var copied=ServicePayload.CopyVerified(source,assemblyPath.Length>0?AppContext.BaseDirectory:null,Path.GetFileNameWithoutExtension(assemblyPath.Length>0?assemblyPath:source),targetRoot,target);
            Directory.CreateDirectory(ServiceRegistration.Root);ProtectData(ServiceRegistration.Root,owner);
            var store=new SettingsStore(Path.Combine(ServiceRegistration.Root,"engine"));
            if(old is null){var initial=setup?.Preferences??EnginePreferences.From(new SettingsStore().Value);initial.Validate();store.Save(initial.Apply(new()));}
            var existed=ServiceRegistration.Installed;
            // Roll back only to the executable Windows itself had registered, never to a path read from a data file.
            var rollback=existed?ServiceInstallTrust.RollbackExecutable(ServiceRegistration.ReadImagePath(),programFiles,File.Exists):null;
            DeviceInfo[] handoff=[];
            using var handoffLog=new AppLog(Path.Combine(ServiceRegistration.Root,"handoff-logs"));
            foreach(var note in securityNotes)handoffLog.Write(note);
            handoffLog.Write($"서비스 사본 준비: {Path.GetFileName(targetRoot)}, 새로 복사·확인한 파일 {copied}개");
            if(existed && rollback is null)handoffLog.Write("기존 서비스 실행 파일 경로를 확인하지 못해 실패 시 실행 파일은 되돌리지 않고 서비스 재시작만 시도함");
            var binary='"'+target+"\" --service";
            void Describe()
            {
                Sc("description",ServiceRegistration.Name,"로그인 전·로그아웃 상태에서도 키보드 전환을 감시하고 마우스를 따라 전환합니다.");
                Sc("failure",ServiceRegistration.Name,"reset=","86400","actions=","restart/1000/restart/3000/restart/10000");
                Sc("failureflag",ServiceRegistration.Name,"1");
            }
            // Settings that do not change the running binary are applied while the old service still follows.
            if(existed)Describe();
            try
            {
                if(existed)
                {
                    using var controller=new ServiceController(ServiceRegistration.Name);
                    if(controller.Status!=ServiceControllerStatus.Stopped)
                    {
                        try{handoff=ServiceDeviceHandoff.CaptureAsync().GetAwaiter().GetResult();}
                        catch(Exception error)when(error is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
                        {handoffLog.Write("이전 서비스 기기 정보 인계 실패: "+error.Message+". 저장 캐시와 기존 선택을 유지하고 새로 검색함");}
                        // From here until the new service runs, no process follows the keyboard; keep this window short.
                        controller.Stop();controller.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(25));
                    }
                }
                ServiceDeviceHandoff.ImportAsync(store.StorageDirectory,handoff,handoffLog).GetAwaiter().GetResult();
                Sc(existed?"config":"create",ServiceRegistration.Name,"binPath=",binary,"start=","auto","obj=","LocalSystem","DisplayName=","EasySwitch Device Service");
                if(!existed)Describe();
                File.WriteAllText(ServiceRegistration.InstallationFile,JsonSerializer.Serialize(new ServiceInstallation(AppVersion.Label,owner,target)));
                Sc("start",ServiceRegistration.Name);
                using var running=new ServiceController(ServiceRegistration.Name);running.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(25));
            }
            catch(Exception original)
            {
                handoffLog.Write("서비스 설치 실패, 이전 상태로 되돌림: "+original);
                if(Rollback(existed,old,rollback,handoffLog) is {} rollbackError)
                    throw new AggregateException("서비스 설치에 실패했고 이전 서비스 복구도 끝내지 못했어.",original,rollbackError);
                throw;
            }
            ServicePayload.RemoveOldCopies(serviceRoot,[targetRoot,rollback is null?null:Path.GetDirectoryName(rollback)],handoffLog);
            using(var startup=Registry.Users.CreateSubKey(owner+@"\Software\Microsoft\Windows\CurrentVersion\Run"))startup.SetValue("EasySwitch",'"'+target+"\" --tray");
            using(var options=Registry.Users.CreateSubKey(owner+@"\Software\EasySwitch")){options.SetValue("BootServiceOptOut",0,RegistryValueKind.DWord);options.DeleteValue(BootServicePolicy.DeclinedVersionValue,false);}
            File.WriteAllText(Path.Combine(ServiceRegistration.Root,"install-result.txt"),$"{DateTimeOffset.Now:O} OK {AppVersion.Label}");return 0;
        }
        catch(Exception error)
        {
            try{Directory.CreateDirectory(ServiceRegistration.Root);File.WriteAllText(Path.Combine(ServiceRegistration.Root,"install-result.txt"),$"{DateTimeOffset.Now:O} {error}");}catch(Exception){}
            return 1;
        }
    }
    public static int Uninstall()
    {
        try
        {
            if(!ServiceRegistration.IsAdmin)throw new UnauthorizedAccessException("서비스 제거에는 관리자 권한이 필요해.");
            var installed=ServiceRegistration.Installation;
            if(installed is null)throw new UnauthorizedAccessException("서비스 등록 정보를 찾지 못했어.");
            using var service=new ServiceController(ServiceRegistration.Name);
            if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(25));}
            Sc("delete",ServiceRegistration.Name);
            using(var startup=Registry.Users.CreateSubKey(installed.OwnerSid+@"\Software\Microsoft\Windows\CurrentVersion\Run"))startup.DeleteValue("EasySwitch",false);
            using(var options=Registry.Users.CreateSubKey(installed.OwnerSid+@"\Software\EasySwitch"))options.SetValue("BootServiceOptOut",1,RegistryValueKind.DWord);
            File.Delete(ServiceRegistration.InstallationFile);return 0;
        }
        catch(Exception error){try{File.WriteAllText(Path.Combine(ServiceRegistration.Root,"install-result.txt"),error.ToString());}catch(Exception){}return 1;}
    }
    // Puts the previous service back after a failed install or update. Every step runs even when an earlier one
    // fails, and each failure is returned so the original error is never hidden behind a rollback error.
    private static Exception? Rollback(bool existed,ServiceInstallation? old,string? rollback,AppLog log)
    {
        var errors=new List<Exception>();
        void Step(string name,Action action)
        {
            try{action();}
            catch(Exception error){errors.Add(new IOException(name+" 실패: "+error.Message,error));log.Write($"되돌리기 단계 실패 - {name}: {error}");}
        }
        Step("새 서비스 중지",StopAndWait);
        if(!existed)
        {
            Step("새 서비스 등록 삭제",()=>{if(ServiceRegistration.Installed)Sc("delete",ServiceRegistration.Name);});
            Step("설치 정보 삭제",()=>{if(File.Exists(ServiceRegistration.InstallationFile))File.Delete(ServiceRegistration.InstallationFile);});
        }
        else
        {
            if(old is not null && rollback is not null)
            {
                Step("이전 설치 정보 복원",()=>File.WriteAllText(ServiceRegistration.InstallationFile,JsonSerializer.Serialize(old with{Executable=rollback})));
                Step("이전 서비스 실행 파일 복원",()=>Sc("config",ServiceRegistration.Name,"binPath=",'"'+rollback+"\" --service"));
            }
            // Restart even without a verified previous executable: a stopped service follows nothing.
            Step("서비스 다시 시작",()=>{Sc("start",ServiceRegistration.Name);using var controller=new ServiceController(ServiceRegistration.Name);controller.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(25));});
        }
        if(errors.Count==0)log.Write(existed?"되돌리기 완료: 이전 서비스가 다시 실행 중":"되돌리기 완료: 새로 등록한 서비스를 삭제함");
        return errors.Count==0?null:new AggregateException("이전 서비스 복구 실패",errors);
    }
    private static void StopAndWait()
    {
        if(!ServiceRegistration.Installed)return;
        using var controller=new ServiceController(ServiceRegistration.Name);
        if(controller.Status==ServiceControllerStatus.Stopped)return;
        if(controller.Status!=ServiceControllerStatus.StopPending)controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(25));
    }
    private static void ProtectData(string path,string owner)
    {
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            acl.AddAccessRule(new(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(owner),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    private static void Sc(params string[] args)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("서비스 등록 도구 시작 실패");
        var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();
        if(!process.WaitForExit(30000))throw new IOException("서비스 등록 시간 초과");
        if(process.ExitCode!=0)throw new IOException($"서비스 등록 실패 {process.ExitCode}: {output} {error}");
    }
}
