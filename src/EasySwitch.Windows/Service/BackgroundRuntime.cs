using System.IO.Pipes;
using System.ServiceProcess;
using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Service;

internal sealed class BackgroundRuntime:IAsyncDisposable
{
    private readonly string _directory,_pipeName,_owner;private readonly bool _fixture;
    private readonly CancellationTokenSource _stop=new();
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;private Task? _server;
    private Dispatcher? _dispatcher;private MainViewModel? _vm;private AppLog? _log;private SettingsStore? _settings;
    private readonly EngineJournal _journal=new();private DispatcherTimer? _health;
    private readonly SemaphoreSlim _clientSlots=new(4,4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task,byte> _clients=new();
    private long _lastHealth=Environment.TickCount64;private bool _suspended,_running;
    private long _powerVersion;
    /// <summary>
    /// How the process ends when the device thread can no longer work. The installed service exits with a
    /// failure code so the Windows service manager's recovery actions restart it. Fixtures record instead.
    /// </summary>
    internal Action<int>? FatalExit{get;set;}
    /// <summary>Longest wait for the device thread to answer one pipe request before the connection is closed.</summary>
    internal TimeSpan DispatchLimit{get;init;}=TimeSpan.FromSeconds(5);
    public BackgroundRuntime(string directory,string pipeName,string owner,bool fixture=false)
    {
        _directory=directory;_pipeName=pipeName;_owner=owner;_fixture=fixture;
        FatalExit=fixture?null:Environment.Exit;
        _thread=new Thread(Run){IsBackground=true,Name="EasySwitch device service"};_thread.SetApartmentState(ApartmentState.STA);
    }
    public async Task StartAsync(){_thread.Start();await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));}
    private void Run()
    {
        try
        {
            _dispatcher=Dispatcher.CurrentDispatcher;
            // A failing callback must not end the thread that receives keyboard departures.
            _dispatcher.UnhandledException+=OnDispatcherException;
            _settings=new SettingsStore(Path.Combine(_directory,"engine"));_log=new AppLog(Path.Combine(_directory,"logs"));
            if(!_fixture)CrashRecorder.Attach(_log);
            if(_settings.LoadError is {} loadError)
            {
                _log.Write($"서비스 설정 파일을 읽지 못함: {loadError}. {(_settings.RestoredFromBackup?"백업 설정으로 시작함":"기본 설정으로 시작함(키보드 따라 전환 꺼짐)")}");
                _log.WriteCode($"{SwitchCode.Prefix} SVC SETTINGS {(_settings.RestoredFromBackup?"BACKUP":"DEFAULT")}");
            }
            _log.Added+=_journal.Add;_log.CodeAdded+=_journal.Add;
            _vm=new(_settings,_log,_dispatcher);
            if(_fixture)_vm.UsePreviewDevices([]);
            _vm.NoticeRequested+=n=>_journal.Add(n,false);_vm.ConnectionNoticeRequested+=n=>_journal.Add(n,true);
            _vm.KeyboardSwitchObserved+=_journal.AddKeyboardSwitch;
            _vm.Start();
            _health=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
            _health.Tick+=(_,_)=>{var now=Environment.TickCount64;var gap=now-_lastHealth;_lastHealth=now;if(!_suspended && gap>10000)Power(false,"실행 공백 감지");};_health.Start();
            _server=ServeAsync();
            _=_server.ContinueWith(t=>{if(t.IsFaulted)Fatal("서비스 통신 서버 중단",t.Exception);},TaskScheduler.Default);
            _running=true;_ready.TrySetResult();Dispatcher.Run();
            if(!_stop.IsCancellationRequested)Fatal("서비스 기기 감시 스레드가 종료 요청 없이 끝남",null);
        }
        catch(Exception error)
        {
            // Before readiness the exception fails OnStart, which the service manager already treats as a failure.
            if(_ready.TrySetException(error))try{_log?.Write("서비스 오류: "+error);}catch(Exception){}
            else if(!_stop.IsCancellationRequested)Fatal("서비스 기기 감시 스레드 오류",error);
            else try{_log?.Write("서비스 종료 중 오류: "+error);}catch(Exception){}
        }
        finally{_running=false;if(_log is not null){CrashRecorder.Detach(_log);_log.Dispose();}}
    }
    private void OnDispatcherException(object sender,DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled=true;
        try{_log?.Write("서비스 처리 중 예외를 기록하고 기기 감시를 계속함: "+e.Exception);}catch(Exception){}
    }
    private void Fatal(string reason,Exception? error)
    {
        var detail=error is null?reason:reason+": "+error;
        try{_log?.Write($"서비스 중단: {detail}. Windows 서비스 관리자가 다시 시작하도록 실패 코드로 종료함");}catch(Exception){}
        if(!_fixture)CrashRecorder.Record("서비스 중단",detail);
        FatalExit?.Invoke(1);
    }
    public void Power(bool suspend,string reason)
    {
        Interlocked.Increment(ref _powerVersion);
        if(suspend)_vm?.BlockForSuspend(reason);
        _dispatcher?.BeginInvoke(async()=>
        {
            if(_stop.IsCancellationRequested || _vm is null)return;
            var now=Environment.TickCount64;
            if(!suspend && !_suspended && now-_lastRecovery<1500)return;
            _suspended=suspend;_lastHealth=now;if(!suspend)_lastRecovery=now;
            try{await _vm.ChangePowerAsync(suspend,reason);}catch(Exception error){_log?.Write("전원 복구 실패: "+error);}
        });
    }
    private long _lastRecovery=long.MinValue/2;
    public void Session(string reason)=>_dispatcher?.BeginInvoke(()=>{_log?.Write("Windows 세션 변경: "+reason+". 서비스 기기 감시는 유지함");_log?.WriteCode($"{SwitchCode.Prefix} SVC SESSION {reason.ToUpperInvariant()}");});
    // Delay before the next pipe attempt. A single failure retries at once, so a client that connects and
    // leaves immediately never slows the next connection; only repeated failures back off.
    internal static int PipeRetryDelay(int consecutiveFailures)=>consecutiveFailures<=1?0:(int)Math.Min(5000,100L<<Math.Min(consecutiveFailures-2,10));
    private async Task ServeAsync()
    {
        var first=true;var failures=0;string? lastFailure=null;
        while(!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe=null;var slot=false;
            try
            {
                await _clientSlots.WaitAsync(_stop.Token);slot=true;
                // Until one instance exists, refuse to join a pipe that another process created under our name.
                pipe=ServiceRegistration.CreatePipe(_pipeName,_owner,_fixture,first);first=false;
                await pipe.WaitForConnectionAsync(_stop.Token);
                if(failures>0)_log?.Write($"서비스 통신 통로 복구: 연속 실패 {failures}회 뒤 연결을 받음");
                failures=0;lastFailure=null;
                var client=HandleClientAsync(pipe);pipe=null;slot=false;_clients.TryAdd(client,0);
                _=client.ContinueWith(t=>_clients.TryRemove(t,out _),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){break;}
            catch(Exception error)
            {
                // The device thread keeps following the keyboard even while the pipe cannot be served.
                failures++;
                var description=error.GetType().Name+": "+error.Message;
                if(description!=lastFailure){lastFailure=description;try{_log?.Write($"서비스 통신 통로 준비 실패(연속 {failures}회): {description}. 다시 준비함");}catch(Exception){}}
                var delay=PipeRetryDelay(failures);
                if(delay>0)try{await Task.Delay(delay,_stop.Token);}catch(OperationCanceledException){break;}
            }
            finally{pipe?.Dispose();if(slot)_clientSlots.Release();}
        }
    }
    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            while(pipe.IsConnected && !_stop.IsCancellationRequested)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(10000);
                var request=await ServiceWire.ReadAsync<ServiceRequest>(pipe,deadline.Token);
                var operation=_dispatcher!.InvokeAsync(()=>Handle(request));
                ServiceReply reply;
                try{reply=await operation.Task.WaitAsync(DispatchLimit,_stop.Token);}
                catch(System.TimeoutException)
                {
                    // Release the slot instead of holding it for a thread that does not answer. A late Switch
                    // is still rejected by its expiry when the operation runs after all.
                    var aborted=operation.Abort();
                    _log?.Write($"서비스 요청 처리 지연: 기기 감시 스레드가 {DispatchLimit.TotalMilliseconds:0}ms 안에 {request.Command} 요청을 처리하지 못해 연결을 닫음({(aborted?"요청 취소":"처리 중")})");
                    return;
                }
                await ServiceWire.WriteAsync(pipe,reply,deadline.Token);
            }
        }
        catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException){}
        finally{await pipe.DisposeAsync();_clientSlots.Release();}
    }
    private ServiceReply Handle(ServiceRequest request)
    {
        try
        {
            if(request.Command is "Switch" or "Refresh" && (request.ExpiresAt<Environment.TickCount64 || request.ExpiresAt>Environment.TickCount64+10000))throw new InvalidDataException("오래된 서비스 요청을 취소했어.");
            switch(request.Command)
            {
                case "Configure":var preferences=request.Preferences??throw new InvalidDataException("설정 없음");preferences.Validate();_vm!.ApplyEnginePreferences(preferences);break;
                case "Switch":
                    if(request.Target is <0 or >2 || request.Reason is null || request.Reason.Length>128)throw new InvalidDataException("잘못된 전환 요청");
                    if(request.PowerGeneration!=Interlocked.Read(ref _powerVersion))throw new InvalidDataException("전원 상태가 바뀌기 전의 전환 요청을 취소했어. 기기 연결을 확인한 뒤 다시 요청해 줘.");
                    _=ObserveSwitchAsync(request.Target,request.Reason);break;
                case "Refresh":_vm!.RefreshCommand.Execute(null);break;
                case "ReadLog":
                    if(request.Offset<0)throw new InvalidDataException("잘못된 로그 위치");
                    // Earlier parts of this session come first, so an export is not cut at a file split.
                    var chunk=_log!.ReadSession(request.Codes,request.Offset,65536);
                    return new(Data:chunk.Data,Length:chunk.Length,LogEpoch:_journal.Epoch+"."+chunk.Epoch);
                case "State":break;
                default:throw new InvalidDataException("지원하지 않는 서비스 요청");
            }
            var journal=_journal.Read(request.Epoch,request.After);
            return new(new(_journal.Epoch,journal.Sequence,_vm!.DeviceSnapshot.ToArray(),EnginePreferences.From(_settings!.Value),_vm.LastDestination,_vm.LastRequest,journal.Events,journal.Truncated,Interlocked.Read(ref _powerVersion)));
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException){return new(Error:error.Message);}
    }
    private async Task ObserveSwitchAsync(int target,string reason)
    {try{await _vm!.SwitchAsync(target,reason);}catch(Exception error){_log?.Write("서비스 전환 오류: "+error);}}
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();if(_server is not null)await _server;await Task.WhenAll(_clients.Keys);
        if(_running && _dispatcher is not null && !_dispatcher.HasShutdownStarted)
        {
            var stop=await _dispatcher.InvokeAsync(async()=>{_health?.Stop();if(_vm is not null)await _vm.DisposeAsync();_dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);});
            await stop;
        }
        if(_thread.IsAlive)await Task.Run(()=>{if(!_thread.Join(20000))throw new System.TimeoutException("서비스 종료 시간 초과");});
    }
}

internal sealed class EasySwitchDeviceService:ServiceBase
{
    private BackgroundRuntime? _runtime;
    public EasySwitchDeviceService(){ServiceName=ServiceRegistration.Name;CanHandlePowerEvent=true;CanHandleSessionChangeEvent=true;CanShutdown=true;AutoLog=false;}
    protected override void OnStart(string[] args)
    {
        try
        {
            var install=ServiceRegistration.Installation??throw new IOException("서비스 설정을 찾지 못했어.");
            _runtime=new(ServiceRegistration.Root,ServiceRegistration.PipeName,install.OwnerSid);_runtime.StartAsync().GetAwaiter().GetResult();
        }
        catch(Exception error)
        {
            // The service log may not exist yet; the dated crash record keeps the reason. Rethrowing reports
            // the failure so the service manager's recovery actions apply.
            CrashRecorder.Record("서비스 시작 실패",error.ToString());
            throw;
        }
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if(powerStatus==PowerBroadcastStatus.Suspend)_runtime?.Power(true,"Windows 절전 진입");
        else if(powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical)_runtime?.Power(false,"Windows 절전 복귀");
        return true;
    }
    protected override void OnSessionChange(SessionChangeDescription changeDescription)=>_runtime?.Session(changeDescription.Reason.ToString());
    protected override void OnStop(){_runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();_runtime=null;}
    protected override void OnShutdown()=>OnStop();
}
