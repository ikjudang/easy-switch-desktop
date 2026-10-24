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
    public BackgroundRuntime(string directory,string pipeName,string owner,bool fixture=false)
    {
        _directory=directory;_pipeName=pipeName;_owner=owner;_fixture=fixture;
        _thread=new Thread(Run){IsBackground=true,Name="EasySwitch device service"};_thread.SetApartmentState(ApartmentState.STA);
    }
    public async Task StartAsync(){_thread.Start();await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));}
    private void Run()
    {
        try
        {
            _dispatcher=Dispatcher.CurrentDispatcher;
            _settings=new SettingsStore(Path.Combine(_directory,"engine"));_log=new AppLog(Path.Combine(_directory,"logs"));
            _log.Added+=_journal.Add;_log.CodeAdded+=_journal.Add;
            _vm=new(_settings,_log,_dispatcher);
            if(_fixture)_vm.UsePreviewDevices([]);
            _vm.NoticeRequested+=n=>_journal.Add(n,false);_vm.ConnectionNoticeRequested+=n=>_journal.Add(n,true);
            _vm.KeyboardSwitchObserved+=_journal.AddKeyboardSwitch;
            _log.Write($"Windows 서비스 {AppVersion.Label} 시작: 로그인 상태와 별개로 기기 감시");
            _log.WriteCode($"{SwitchCode.Prefix} SVC START");_vm.Start();
            _health=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
            _health.Tick+=(_,_)=>{var now=Environment.TickCount64;var gap=now-_lastHealth;_lastHealth=now;if(!_suspended && gap>10000)Power(false,"실행 공백 감지");};_health.Start();
            _server=ServeAsync();
            _=_server.ContinueWith(t=>{if(t.IsFaulted){try{_log.Write("서비스 통신 서버 중단: "+t.Exception);}catch(Exception){}if(!_fixture)Environment.Exit(1);}},TaskScheduler.Default);
            _running=true;_ready.TrySetResult();Dispatcher.Run();
        }
        catch(Exception error){_ready.TrySetException(error);try{_log?.Write("서비스 오류: "+error);}catch(Exception){}}
        finally{_running=false;_log?.Dispose();}
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
    private async Task ServeAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                await _clientSlots.WaitAsync(_stop.Token);
                NamedPipeServerStream? pipe=null;
                try{pipe=ServiceRegistration.CreatePipe(_pipeName,_owner,_fixture);await pipe.WaitForConnectionAsync(_stop.Token);}
                catch{pipe?.Dispose();_clientSlots.Release();throw;}
                var client=HandleClientAsync(pipe);_clients.TryAdd(client,0);
                _=client.ContinueWith(t=>_clients.TryRemove(t,out _),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
            catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {if(_stop.IsCancellationRequested)break;}
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
                var reply=await _dispatcher!.InvokeAsync(()=>Handle(request));await ServiceWire.WriteAsync(pipe,reply,deadline.Token);
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
                    using(var file=new FileStream(request.Codes?_log!.CodeFilePath:_log!.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                    {
                        var length=file.Length;if(request.Offset>length)throw new InvalidDataException("로그 크기를 벗어난 위치");file.Position=request.Offset;
                        var bytes=new byte[(int)Math.Min(65536,length-request.Offset)];file.ReadExactly(bytes);return new(Data:bytes,Length:length,LogEpoch:_journal.Epoch);
                    }
                case "State":break;
                default:throw new InvalidDataException("지원하지 않는 서비스 요청");
            }
            var journal=_journal.Read(request.Epoch,request.After);
            return new(new(_journal.Epoch,journal.Sequence,_vm!.Devices.ToArray(),EnginePreferences.From(_settings!.Value),_vm.LastDestination,_vm.LastRequest,journal.Events,journal.Truncated,Interlocked.Read(ref _powerVersion)));
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
        var install=ServiceRegistration.Installation??throw new IOException("서비스 설정을 찾지 못했어.");
        _runtime=new(ServiceRegistration.Root,ServiceRegistration.PipeName,install.OwnerSid);_runtime.StartAsync().GetAwaiter().GetResult();
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
