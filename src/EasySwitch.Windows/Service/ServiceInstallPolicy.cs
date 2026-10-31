using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Service;

internal enum BootServiceAction{Skip,Wait,Prompt}
internal sealed record BootServiceDecision(BootServiceAction Action,string Reason,TimeSpan Wait=default);
/// <summary>What the app can see about the registered service without administrator rights.</summary>
internal sealed record BootServiceState(bool Registered,ServiceControllerStatus? Status,bool StoppedByFailure);

// Decides whether a normal app start may ask for administrator approval. The explicit tray command
// passes force and can always ask; automatic starts avoid prompts that cannot help.
internal static class BootServicePolicy
{
    internal const string DeclinedVersionValue="BootServiceDeclinedVersion";
    internal static readonly TimeSpan StartWait=TimeSpan.FromSeconds(20);
    // The registered recovery actions restart a failed service after 1, 3 and then 10 seconds.
    internal static readonly TimeSpan RestartWait=TimeSpan.FromSeconds(12);
    internal static BootServiceDecision Decide(bool force,bool optedOut,string? declinedVersion,ServiceInstallation? installed,BootServiceState state,
        string currentSid,Version current,string currentLabel,bool sourceRun,bool waited)
    {
        if(!force && optedOut)return new(BootServiceAction.Skip,"로그인 전 실행: 사용자가 서비스를 제거해 자동 설치를 요청하지 않음. 트레이의 '로그인 전 실행 설치·업데이트'로 다시 설치할 수 있음");
        if(Version.TryParse(installed?.Version,out var installedVersion) && installedVersion>current)
            return new(BootServiceAction.Skip,$"로그인 전 실행: 더 최신 서비스 {installed!.Version}가 설치되어 있어 설치를 요청하지 않음");
        var sameVersion=state.Registered && installed?.Version==currentLabel;
        if(sameVersion)
        {
            if(state.Status==ServiceControllerStatus.Running)return new(BootServiceAction.Skip,"로그인 전 실행: 같은 버전 서비스가 실행 중");
            if(!waited && state.Status is ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.StopPending)
                return new(BootServiceAction.Wait,$"로그인 전 실행: 같은 버전 서비스가 {state.Status} 상태라 실행될 때까지 최대 {StartWait.TotalSeconds:0}초 기다림",StartWait);
            if(!waited && state.Status==ServiceControllerStatus.Stopped && state.StoppedByFailure)
                return new(BootServiceAction.Wait,$"로그인 전 실행: 같은 버전 서비스가 비정상 종료되어 Windows의 자동 재시작을 최대 {RestartWait.TotalSeconds:0}초 기다림",RestartWait);
        }
        if(installed is not null && installed.OwnerSid!=currentSid)return new(BootServiceAction.Skip,"로그인 전 실행: 다른 Windows 계정이 관리하는 서비스 설정을 유지함");
        if(sourceRun)return new(BootServiceAction.Skip,"소스 실행: 서비스 설치를 생략함");
        if(!force && declinedVersion==currentLabel)
            return new(BootServiceAction.Skip,$"로그인 전 실행: 사용자가 {currentLabel} 서비스 승인 요청을 취소해 자동으로 다시 묻지 않음. 서비스가 실행 중이 아니면 키보드 따라 전환도 멈춰 있음. 트레이의 '로그인 전 실행 설치·업데이트'로 다시 요청할 수 있음");
        // A stopped service of this version is the only follower this PC has: without it nothing follows the
        // keyboard, so the app asks to start it again rather than staying silent.
        return new(BootServiceAction.Prompt,sameVersion
            ?$"로그인 전 실행: 같은 버전 서비스가 {(state.Status?.ToString()??"알 수 없는")} 상태라 키보드 따라 전환이 멈춰 있어 서비스 시작 승인을 요청함"
            :"로그인 전 실행: 서비스 설치·업데이트 승인을 요청함");
    }
}

internal static class ServiceInstallTrust
{
    internal static readonly Regex CopyFolder=new(@"\A\d+\.\d+\.\d+-[0-9A-F]{12}\z",RegexOptions.CultureInvariant);
    internal const string ServiceExecutable="EasySwitch.Service.exe";
    internal static bool IsTrustedOwner(SecurityIdentifier? owner)=>
        owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    /// <summary>Returns the quoted executable of a service image path, or null when it is not in the form the installer writes.</summary>
    internal static string? ExecutableFromImagePath(string? imagePath)
    {
        if(string.IsNullOrWhiteSpace(imagePath))return null;
        var text=imagePath.Trim();
        if(text.Length<2 || text[0]!='"')return null;
        var end=text.IndexOf('"',1);
        return end>1?text[1..end]:null;
    }
    /// <summary>
    /// The only executable a failed update may hand back to the LocalSystem service: an existing
    /// EasySwitch.Service.exe directly inside one version folder under Program Files\EasySwitch\Service.
    /// </summary>
    internal static string? RollbackExecutable(string? imagePath,string programFiles,Func<string,bool> exists)
    {
        if(ExecutableFromImagePath(imagePath) is not {} executable)return null;
        string full;
        try
        {
            if(!Path.IsPathFullyQualified(executable))return null;
            full=Path.GetFullPath(executable);
        }
        catch(Exception error)when(error is ArgumentException or NotSupportedException or PathTooLongException){return null;}
        var root=Path.GetFullPath(Path.Combine(programFiles,"EasySwitch","Service"))+Path.DirectorySeparatorChar;
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))return null;
        var parts=full[root.Length..].Split(Path.DirectorySeparatorChar);
        if(parts.Length!=2 || !CopyFolder.IsMatch(parts[0]) || !parts[1].Equals(ServiceExecutable,StringComparison.OrdinalIgnoreCase))return null;
        return exists(full)?full:null;
    }
    /// <summary>
    /// Makes Administrators the owner of the service data folders when another account owns them, and removes
    /// the grants that account could use to change them again. Runs elevated before any file in them is read.
    /// </summary>
    internal static List<string> SecureDataRoot(string root,string ownerSid)
    {
        var notes=new List<string>();
        var parent=Path.GetDirectoryName(root)!;
        // A link would send the installer's and the SYSTEM service's writes to a folder someone else chose,
        // while ACL changes would only reach the link itself. Refuse before touching anything.
        RefuseLink(parent);RefuseLink(root);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        const InheritanceFlags inherit=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        Directory.CreateDirectory(parent);
        var parentInfo=new DirectoryInfo(parent);
        var parentOwner=parentInfo.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if(!IsTrustedOwner(parentOwner))
        {
            var acl=new DirectorySecurity();acl.SetOwner(administrators);acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new(system,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new(administrators,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,inherit,PropagationFlags.None,AccessControlType.Allow));
            parentInfo.SetAccessControl(acl);
            notes.Add($"서비스 데이터 상위 폴더 소유자 복구: {parentOwner?.Value??"알 수 없음"} → Administrators");
        }
        Directory.CreateDirectory(root);
        var rootInfo=new DirectoryInfo(root);
        var rootOwner=rootInfo.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if(IsTrustedOwner(rootOwner))return notes;
        var protectedAcl=new DirectorySecurity();protectedAcl.SetOwner(administrators);protectedAcl.SetAccessRuleProtection(true,false);
        protectedAcl.AddAccessRule(new(system,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
        protectedAcl.AddAccessRule(new(administrators,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
        protectedAcl.AddAccessRule(new(new SecurityIdentifier(ownerSid),FileSystemRights.ReadAndExecute,inherit,PropagationFlags.None,AccessControlType.Allow));
        rootInfo.SetAccessControl(protectedAcl);
        // The previous owner could have swapped the folder for a link until this moment.
        RefuseLink(root);
        notes.Add($"서비스 데이터 폴더 소유자 복구: {rootOwner?.Value??"알 수 없음"} → Administrators");
        // Entries created while another account owned the folder may carry their own grants: reset them to inherit.
        var counts=ResetEntries(rootInfo,entry=>
        {
            if(entry is DirectoryInfo directory){var s=new DirectorySecurity();s.SetOwner(administrators);s.SetAccessRuleProtection(false,false);directory.SetAccessControl(s);}
            else if(entry is FileInfo file){var s=new FileSecurity();s.SetOwner(administrators);s.SetAccessRuleProtection(false,false);file.SetAccessControl(s);}
        },notes);
        notes.Add($"서비스 데이터 하위 항목 권한 재설정: {counts.Reset}개 완료, 연결 항목 {counts.Links}개 삭제, {counts.Failed}개 실패");
        return notes;
    }
    internal static void RefuseLink(string path)
    {
        if((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new IOException($"서비스 데이터 위치 {path}가 다른 위치로 연결된 항목이라 설치를 중단했어. 관리자가 이 연결을 지운 뒤 다시 설치해 줘.");
    }
    /// <summary>
    /// Walks the folder without following links. Links are removed (this app never creates them, and following
    /// one would act on another location); every other entry is passed to <paramref name="reset"/>.
    /// </summary>
    internal static (int Reset,int Links,int Failed) ResetEntries(DirectoryInfo root,Action<FileSystemInfo> reset,List<string> notes)
    {
        int done=0,links=0,failed=0;
        var options=new EnumerationOptions{RecurseSubdirectories=false,IgnoreInaccessible=false,AttributesToSkip=0};
        var pending=new Stack<DirectoryInfo>();pending.Push(root);
        while(pending.Count>0)
        {
            foreach(var entry in pending.Pop().EnumerateFileSystemInfos("*",options))
            {
                try
                {
                    if((entry.Attributes&FileAttributes.ReparsePoint)!=0){entry.Delete();links++;notes.Add($"서비스 데이터 안의 연결 항목 삭제: {entry.FullName}");continue;}
                    reset(entry);done++;
                    if(entry is DirectoryInfo directory)pending.Push(directory);
                }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException){failed++;notes.Add($"서비스 데이터 권한 재설정 실패: {entry.FullName}: {error.Message}");}
            }
        }
        return(done,links,failed);
    }
}

// Decides which files make up a folder deployment and copies them into the service folder with verification.
internal static class ServicePayload
{
    private static readonly Regex Culture=new(@"\A[a-z]{2,3}(-[A-Za-z0-9]{2,8})*\z",RegexOptions.CultureInvariant);
    /// <summary>
    /// Relative paths of the app's own files: the app and its configuration, every asset its .deps.json
    /// lists, satellite resources of those assemblies, and the bundled license texts. Unrelated files that
    /// happen to share the folder (for example a Downloads folder) are not included.
    /// </summary>
    internal static IReadOnlyList<string> Plan(string baseDirectory,string appName)
    {
        var root=Path.GetFullPath(baseDirectory);
        var deps=Path.Combine(root,appName+".deps.json");
        if(!File.Exists(deps))throw new IOException($"앱 구성 파일 {appName}.deps.json을 찾지 못해 서비스 사본을 만들지 않았어.");
        var files=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool TryAdd(string relative)
        {
            var full=Path.GetFullPath(Path.Combine(root,relative));
            if(!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !File.Exists(full))return false;
            files.Add(Path.GetRelativePath(root,full));return true;
        }
        void Require(string relative){if(!TryAdd(relative))throw new IOException($"서비스 사본에 필요한 파일 {relative}이 앱 폴더에 없어.");}
        Require(appName+".dll");Require(appName+".deps.json");Require(appName+".runtimeconfig.json");TryAdd(appName+".exe");
        using(var document=JsonDocument.Parse(File.ReadAllBytes(deps)))
        {
            var targetName=document.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()??throw new InvalidDataException("앱 구성 파일의 대상 정보가 없어.");
            foreach(var library in document.RootElement.GetProperty("targets").GetProperty(targetName).EnumerateObject())
            {
                foreach(var kind in new[]{"runtime","native"})
                    if(library.Value.TryGetProperty(kind,out var assets))
                        foreach(var asset in assets.EnumerateObject())
                            if(!TryAdd(asset.Name) && !TryAdd(Path.GetFileName(asset.Name)))throw new IOException($"서비스 사본에 필요한 파일 {asset.Name}이 앱 폴더에 없어.");
                if(library.Value.TryGetProperty("resources",out var resources))
                    foreach(var asset in resources.EnumerateObject())
                    {
                        var locale=asset.Value.TryGetProperty("locale",out var value)?value.GetString():null;
                        if(!TryAdd(asset.Name) && !(locale is not null && TryAdd(Path.Combine(locale,Path.GetFileName(asset.Name)))))
                            throw new IOException($"서비스 사본에 필요한 파일 {asset.Name}이 앱 폴더에 없어.");
                    }
                // Framework-dependent builds keep platform-specific assets in runtimes\<rid>\...; copy those present.
                if(library.Value.TryGetProperty("runtimeTargets",out var targets))
                    foreach(var asset in targets.EnumerateObject())TryAdd(asset.Name);
            }
        }
        // Satellite resources of the bundled runtime are not listed in .deps.json; keep only those whose assembly is copied.
        foreach(var directory in Directory.EnumerateDirectories(root))
        {
            var culture=Path.GetFileName(directory);
            if(!Culture.IsMatch(culture))continue;
            foreach(var satellite in Directory.EnumerateFiles(directory,"*.resources.dll"))
            {
                var name=Path.GetFileName(satellite);
                if(files.Contains(name[..^".resources.dll".Length]+".dll"))files.Add(Path.Combine(culture,name));
            }
        }
        var licenses=Path.Combine(root,"Assets","Licenses");
        if(Directory.Exists(licenses))foreach(var license in Directory.EnumerateFiles(licenses))files.Add(Path.GetRelativePath(root,license));
        return files.ToArray();
    }
    /// <summary>
    /// Copies the plan and the executable into the service folder, skipping files that already match and
    /// verifying each copy by SHA-256. Throws before the running service is touched when anything differs.
    /// </summary>
    internal static int CopyVerified(string sourceExecutable,string? baseDirectory,string appName,string targetRoot,string targetExecutable)
    {
        var copies=new List<(string From,string To)>();
        if(baseDirectory is not null && !SameDirectory(baseDirectory,targetRoot))
            foreach(var relative in Plan(baseDirectory,appName))copies.Add((Path.Combine(baseDirectory,relative),Path.Combine(targetRoot,relative)));
        if(!string.Equals(Path.GetFullPath(sourceExecutable),Path.GetFullPath(targetExecutable),StringComparison.OrdinalIgnoreCase))copies.Add((sourceExecutable,targetExecutable));
        var copied=0;
        foreach(var (from,to) in copies)
        {
            var expected=Hash(from);
            if(File.Exists(to) && Hash(to).AsSpan().SequenceEqual(expected))continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(from,to,true);copied++;
            if(!Hash(to).AsSpan().SequenceEqual(expected))throw new IOException($"서비스 사본 확인 실패: {Path.GetRelativePath(targetRoot,to)}의 내용이 원본과 달라.");
        }
        return copied;
    }
    private static bool SameDirectory(string a,string b)=>string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
    private static byte[] Hash(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }
    /// <summary>
    /// Removes earlier version folders after the new service runs. A folder whose program is still running
    /// (its executable cannot be deleted) is left untouched; folders not named like a service copy are ignored.
    /// </summary>
    internal static (int Removed,int InUse,int Failed) RemoveOldCopies(string serviceRoot,IEnumerable<string?> keep,AppLog log)
    {
        if(!Directory.Exists(serviceRoot))return(0,0,0);
        var kept=new HashSet<string>(keep.OfType<string>().Select(k=>Path.GetFullPath(k).TrimEnd(Path.DirectorySeparatorChar)),StringComparer.OrdinalIgnoreCase);
        int removed=0,inUse=0,failed=0;
        foreach(var directory in Directory.EnumerateDirectories(serviceRoot))
        {
            var full=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if(kept.Contains(full) || !ServiceInstallTrust.CopyFolder.IsMatch(Path.GetFileName(full)))continue;
            if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0){log.Write($"이전 서비스 사본 정리 생략: {Path.GetFileName(full)}이 연결 폴더임");continue;}
            var executables=Directory.EnumerateFiles(full,"*.exe").OrderBy(f=>Path.GetFileName(f).Equals(ServiceInstallTrust.ServiceExecutable,StringComparison.OrdinalIgnoreCase)?0:1).ToArray();
            string? busy=null;
            foreach(var executable in executables)
            {
                try{File.Delete(executable);}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){busy=Path.GetFileName(executable)+": "+error.Message;break;}
            }
            if(busy is not null){inUse++;log.Write($"이전 서비스 사본 정리 생략: {Path.GetFileName(full)}의 프로그램이 실행 중이라 남김({busy})");continue;}
            try{Directory.Delete(full,true);removed++;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){failed++;log.Write($"이전 서비스 사본 일부를 지우지 못함: {Path.GetFileName(full)}: {error.Message}. 다음 설치 때 다시 정리함");}
        }
        log.Write($"이전 서비스 사본 정리: {removed}개 삭제, 실행 중이라 남김 {inUse}개, 실패 {failed}개");
        return(removed,inUse,failed);
    }
}
