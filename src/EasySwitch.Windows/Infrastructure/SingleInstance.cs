using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace EasySwitch.Windows.Infrastructure;

// Keep the original mutex and activation names so pre-upgrade executables cannot run alongside us.
// Mutex ownership is thread-affine: Enter and Dispose run on the WPF dispatcher thread.
internal sealed class SingleInstance(string? fixtureScope = null, string? ownerDirectory = null) : IDisposable
{
    private readonly string _scope = fixtureScope ?? "EasySwitch.Windows";
    private readonly string _ownerFile = Path.Combine(ownerDirectory ?? SettingsStore.DataDirectory,
        $"instance-{Process.GetCurrentProcess().SessionId}{(fixtureScope is null ? "" : "-" + fixtureScope)}.json");
    private Mutex? _instance;
    private EventWaitHandle? _activate, _stop;
    private RegisteredWaitHandle? _activateWait, _stopWait;
    private bool _owns;
    public List<string> Messages { get; } = [];

    public bool Enter(Version version, Action activate, Action stop)
    {
        using var gate = new Mutex(false, $"Local\\{_scope}.Launch");
        if (!Take(gate, TimeSpan.FromSeconds(20))) throw new IOException("다른 앱 실행 작업이 끝나지 않았어. 잠시 후 새 파일을 다시 실행해 줘.");
        try
        {
            _instance = new Mutex(false, $"Local\\{_scope}.0");
            if (!Take(_instance, TimeSpan.Zero))
            {
                using var owner = FindOwner();
                if (owner is null) throw new IOException("실행 중인 앱의 버전을 확인하지 못했어. 트레이에서 기존 앱을 종료한 뒤 새 파일을 실행해 줘.");
                if (owner.Version >= version)
                {
                    try { using var signal = EventWaitHandle.OpenExisting($"Local\\{_scope}.Activate"); signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return false;
                }
                var graceful = false;
                if (owner.StopEvent is { } name)
                {
                    try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); graceful = owner.Process.WaitForExit(7000); }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
                if (!graceful && !owner.Process.HasExited)
                {
                    // 0.2.2 and earlier have no shutdown IPC. Only a positively identified, older,
                    // same-user/same-session EasySwitch process is eligible for this fallback.
                    if (!IsOurProcess(owner.Process)) throw new IOException("기존 앱의 실행 정보를 재확인하지 못해 종료하지 않았어.");
                    owner.Process.Kill();
                    if (!owner.Process.WaitForExit(5000)) throw new IOException("이전 앱이 종료되지 않아 새 앱의 중복 실행을 중단했어.");
                }
                Messages.Add($"이전 버전 {owner.Version.ToString(3)} 종료 확인 → {version.ToString(3)} 시작 ({(graceful ? "정상 종료" : "이전 프로세스 종료")})");
                if (!Take(_instance, TimeSpan.FromSeconds(5))) throw new IOException("이전 앱의 기기 통로 해제가 끝나지 않아 새 앱의 중복 실행을 중단했어.");
            }
            _owns = true;
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{_scope}.Activate");
            var started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            var stopName = $"Local\\{_scope}.Stop.{Environment.ProcessId}.{started}";
            _stop = new EventWaitHandle(false, EventResetMode.AutoReset, stopName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => activate(), null, Timeout.Infinite, false);
            _stopWait = ThreadPool.RegisterWaitForSingleObject(_stop, (_, _) => stop(), null, Timeout.Infinite, true);
            Directory.CreateDirectory(Path.GetDirectoryName(_ownerFile)!);
            var temp = _ownerFile + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new OwnerRecord(Environment.ProcessId, started, version.ToString(), stopName)));
            File.Move(temp, _ownerFile, true);
            return true;
        }
        finally { gate.ReleaseMutex(); }
    }

    private Owner? FindOwner()
    {
        try
        {
            var record = JsonSerializer.Deserialize<OwnerRecord>(File.ReadAllText(_ownerFile));
            if (record is not null && Version.TryParse(record.Version, out var version))
            {
                var process = Process.GetProcessById(record.Pid);
                if (process.StartTime.ToUniversalTime().Ticks == record.Started && IsOurProcess(process))
                    return new(process, version, record.StopEvent);
                process.Dispose();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or Win32Exception or InvalidOperationException) { }

        // Legacy builds have no owner record. Enumerate hidden main windows too: closing their
        // main window only hides it, so CloseMainWindow cannot perform a version replacement.
        var ids = new HashSet<int>();
        EnumWindows((window, _) =>
        {
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "EasySwitch") { GetWindowThreadProcessId(window, out var pid); ids.Add((int)pid); }
            return true;
        }, 0);
        var candidates = new List<Owner>();
        foreach (var id in ids.Where(id => id != Environment.ProcessId))
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(id);
                if (!IsOurProcess(process)) continue;
                var info = process.MainModule!.FileVersionInfo;
                var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                // Newer builds must identify themselves through the owner record; never guess.
                if (version > new Version(0, 2, 2, 0)) continue;
                candidates.Add(new(process, version, null)); process = null;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
            finally { process?.Dispose(); }
        }
        if (candidates.Count == 1) return candidates[0];
        foreach (var candidate in candidates) candidate.Dispose();
        return null;
    }

    private static bool IsOurProcess(Process process)
    {
        if (process.Id == Environment.ProcessId || process.HasExited || process.SessionId != Process.GetCurrentProcess().SessionId) return false;
        if (!OpenProcessToken(process.SafeHandle, 8, out var token)) return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            if (identity.User != current.User) return false;
        var info = process.MainModule?.FileVersionInfo;
        return info?.ProductName == "EasySwitch" && info.OriginalFilename == "EasySwitch.dll" && info.FileDescription == "EasySwitch";
    }

    private static bool Take(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }
    public void Dispose()
    {
        _activateWait?.Unregister(null); _stopWait?.Unregister(null);
        _activate?.Dispose(); _stop?.Dispose();
        if (_owns)
        {
            try { File.Delete(_ownerFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _instance!.ReleaseMutex(); _owns = false;
        }
        _instance?.Dispose();
    }
    private sealed record OwnerRecord(int Pid, long Started, string Version, string StopEvent);
    private sealed record Owner(Process Process, Version Version, string? StopEvent) : IDisposable { public void Dispose() => Process.Dispose(); }
    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
