using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Service;

internal sealed class ServiceClient(AppLog log,string? fixturePipe=null,int? fixturePid=null):IAsyncDisposable
{
    private sealed record Pending(ServiceRequest Request,TaskCompletionSource<ServiceReply> Completion);
    private sealed record Configuration(EnginePreferences Preferences,long Revision);
    internal sealed record JournalCursor(string Epoch,long Sequence);
    private readonly Channel<Pending> _requests=Channel.CreateBounded<Pending>(256);
    private readonly CancellationTokenSource _stop=new();private Task? _loop;
    private readonly object _configurationGate=new();
    private Configuration? _preferences;
    private EnginePreferences? _restoredPreferences;
    private volatile bool _compareInitialPreferences;
    private long _configurationRevision,_acknowledgedRevision,_connectionGeneration;
    private string? _epoch,_lastError,_lastHandlerError;private long _after,_powerGeneration;private volatile bool _connected;
    private volatile bool _configuring;
    private JournalCursor? _previousRecord,_delivered;private long _cursorSavedAt=long.MinValue/2;private int _cursorSaving;private string? _cursorError;
    public bool Connected=>_connected;
    public bool ConfigurationPending=>_configuring || _compareInitialPreferences || Volatile.Read(ref _preferences) is not null || ConfigurationRevision>Interlocked.Read(ref _acknowledgedRevision);
    internal long ConfigurationRevision=>Interlocked.Read(ref _configurationRevision);
    internal long ConnectionGeneration=>Interlocked.Read(ref _connectionGeneration);
    /// <summary>Where this account remembers how far the service journal was already written to its UI log.</summary>
    internal string? CursorFile{get;init;}=fixturePipe is null?Path.Combine(SettingsStore.DataDirectory,"service-journal-cursor.json"):null;
    public event Action<ServiceSnapshot>? SnapshotReceived;
    public event Action<EnginePreferences>? InitialPreferencesCompared;
    public event Action<bool>? AvailabilityChanged;
    public void Start()=>_loop??=Task.Run(RunAsync);
    public void RestorePreferences(EnginePreferences preferences)
    {
        lock(_configurationGate)
        {
            if(_loop is not null)throw new InvalidOperationException("서비스 시작 전에 복원 설정을 지정해야 해.");
            if(_configurationRevision!=0)return;
            _restoredPreferences=preferences;_compareInitialPreferences=true;
        }
    }
    public void Configure(EnginePreferences preferences)
    {
        lock(_configurationGate)
        {
            _compareInitialPreferences=false;_restoredPreferences=null;
            Interlocked.Exchange(ref _preferences,new(preferences,Interlocked.Increment(ref _configurationRevision)));
        }
    }
    internal bool IsCurrent(ServiceSnapshot snapshot)=>Connected && snapshot.ClientConnection==ConnectionGeneration && snapshot.Epoch==Volatile.Read(ref _epoch);
    internal bool CanApplyPreferences(ServiceSnapshot snapshot)=>IsCurrent(snapshot) && !ConfigurationPending && snapshot.ClientRevision==ConfigurationRevision;
    private void CompareInitialPreferences(ServiceSnapshot state,long revision)
    {
        EnginePreferences? restored;
        lock(_configurationGate)restored=_compareInitialPreferences && revision==_configurationRevision?_restoredPreferences:null;
        if(restored is null)return;
        // The subscriber keeps a backup of both selections. If writing it fails, the comparison still has to finish:
        // throwing here would drop the pipe and fail again on every reconnect, leaving manual switching unusable.
        try{InitialPreferencesCompared?.Invoke(state.Preferences);}
        catch(Exception error){log.Write("서비스 초기 동기화의 선택 백업 실패(동기화는 계속함): "+error);}
        bool restore;
        lock(_configurationGate)
        {
            // A user edit made during the comparison takes precedence over startup restoration.
            if(!_compareInitialPreferences || revision!=_configurationRevision)return;
            restore=restored.PreferRestoredOver(state.Preferences);
            _compareInitialPreferences=false;_restoredPreferences=null;
            if(restore)Interlocked.Exchange(ref _preferences,new(restored,Interlocked.Increment(ref _configurationRevision)));
        }
        log.Write($"서비스 초기 동기화: {(restore?"앱 저장 선택 적용 대기":"서비스 저장 설정 확인")} (앱 revision={restored.Revision}, 서비스 revision={state.Preferences.Revision})");
    }
    public Task<ServiceReply> SwitchAsync(int target,string reason)=>RequestAsync(new("Switch",Target:target,Reason:reason,ExpiresAt:Environment.TickCount64+2000,PowerGeneration:Interlocked.Read(ref _powerGeneration)));
    public Task<ServiceReply> RefreshAsync()=>RequestAsync(new("Refresh",ExpiresAt:Environment.TickCount64+3000));
    public async Task ExportAsync(string path,bool codes)
    {
        // The service only supplies bytes from its own fixed log. The UI opens the user path.
        var temp=path+".easyswitch-"+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                var offset=0L;long? length=null;string? epoch=null;
                do
                {
                    var reply=await RequestAsync(new("ReadLog",Codes:codes,Offset:offset));length??=reply.Length;epoch??=reply.LogEpoch;
                    if(epoch!=reply.LogEpoch)throw new IOException("내보내기 중 서비스가 다시 시작되었거나 오래된 로그 파일이 정리되어 로그가 바뀌었어. 다시 내보내 줘.");
                    var data=reply.Data??[];var count=(int)Math.Min(data.Length,length.Value-offset);
                    if(count<=0 && offset<length)throw new IOException("서비스 로그를 끝까지 읽지 못했어.");
                    await output.WriteAsync(data.AsMemory(0,count));offset+=count;
                }while(offset<length);
            }
            File.Move(temp,path,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private async Task<ServiceReply> RequestAsync(ServiceRequest request)
    {
        if(!Connected)throw new IOException("서비스가 다시 연결되는 중이야. 연결 후 다시 요청해 줘.");
        var completion=new TaskCompletionSource<ServiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_requests.Writer.TryWrite(new(request,completion)))throw new IOException("서비스 요청 대기 한도를 초과했어.");
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(6),_stop.Token);
    }
    // Waits between connection attempts. Each attempt itself waits up to one second for the pipe, so a
    // service that is restarting is picked up quickly; repeated failures only slow the loop to the cap.
    internal static int RetryDelay(int consecutiveFailures)=>consecutiveFailures<=1?500:1000;
    private async Task RunAsync()
    {
        LoadCursor();
        var failures=0;
        // The loop must outlive any single failure: when it ends, the UI shows devices offline until restart.
        while(!_stop.IsCancellationRequested)
        {
            Pending? current=null;
            Configuration? sendingPreferences=null;
            try
            {
                await using var pipe=new NamedPipeClientStream(".",fixturePipe??ServiceRegistration.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(1000,_stop.Token);ServiceRegistration.VerifyServer(pipe,fixturePid);
                var connection=Interlocked.Increment(ref _connectionGeneration);
                if(failures>0)log.Write($"서비스 연결 복구: 연속 실패 {failures}회 뒤 연결함");
                _connected=true;_lastError=null;failures=0;NotifyAvailability(true);
                while(!_stop.IsCancellationRequested)
                {
                    Configuration? preferences;long revision;
                    lock(_configurationGate)
                    {
                        preferences=Interlocked.Exchange(ref _preferences,null);_configuring=preferences is not null;
                        revision=preferences?.Revision??Interlocked.Read(ref _acknowledgedRevision);
                    }
                    sendingPreferences=preferences;
                    current=null;ServiceRequest request;
                    if(preferences is not null)request=new("Configure",Preferences:preferences.Preferences);
                    else if(_requests.Reader.TryRead(out current))request=current.Request;
                    else request=new();
                    request=request with{Epoch=_epoch,After=_after};
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(4000);
                    await ServiceWire.WriteAsync(pipe,request,deadline.Token);
                    var reply=await ServiceWire.ReadAsync<ServiceReply>(pipe,deadline.Token);
                    if(preferences is not null)
                    {
                        if(reply.Error is not null || reply.State is not {} confirmed || confirmed.Preferences.ContentFingerprint!=preferences.Preferences.ContentFingerprint)
                            throw new IOException("서비스 설정 저장을 확인하지 못했어: "+(reply.Error??"요청한 설정과 응답이 달라."));
                        Interlocked.Exchange(ref _acknowledgedRevision,preferences.Revision);_configuring=false;
                    }
                    if(reply.State is {} state)
                    {
                        CompareInitialPreferences(state,revision);
                        Interlocked.Exchange(ref _powerGeneration,state.PowerGeneration);Volatile.Write(ref _epoch,state.Epoch);_after=state.Sequence;
                        var recorded=_previousRecord is {} earlier && earlier.Epoch==state.Epoch?earlier.Sequence:0;
                        Deliver(state with{ClientConnection=connection,ClientRevision=revision,ClientRecordedThrough=recorded});
                        if(state.Events.Length>0)NoteDelivered(state.Epoch,state.Sequence);
                    }
                    sendingPreferences=null;
                    if(reply.Error is {} error){if(current is null)log.Write("서비스 요청 실패: "+error);else current.Completion.TrySetException(new IOException(error));}
                    else current?.Completion.TrySetResult(reply);
                    current=null;
                    if(request.Command=="State")
                    {
                        // A queued request (a Switch above all) ends this wait at once.
                        using var wait=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);wait.CancelAfter(100);
                        try{await _requests.Reader.WaitToReadAsync(wait.Token);}catch(OperationCanceledException)when(!_stop.IsCancellationRequested){}
                    }
                }
            }
            catch(Exception error)
            {
                current?.Completion.TrySetException(new IOException("서비스 요청 결과를 확인하지 못했어. 자동으로 재전송하지 않아.",error));
                if(_connected){_connected=false;NotifyAvailability(false);}
                if(sendingPreferences is not null)Interlocked.CompareExchange(ref _preferences,sendingPreferences,null);
                _configuring=false;
                while(_requests.Reader.TryRead(out var pending))pending.Completion.TrySetException(new IOException("서비스 연결이 끊겨 요청을 취소했어."));
                if(_stop.IsCancellationRequested)break;
                failures++;
                if(_lastError!=error.GetType().Name){_lastError=error.GetType().Name;log.Write(RetryMessage(error));}
                try{await Task.Delay(RetryDelay(failures),_stop.Token);}catch(OperationCanceledException){break;}
            }
        }
    }
    private static string RetryMessage(Exception error)=>error switch
    {
        TimeoutException=>"서비스 연결 재시도: 1초 안에 서비스 통신 통로에 연결하지 못함(서비스가 시작 중이거나 멈춰 있음)",
        IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidDataException or JsonException=>"서비스 연결 재시도: "+error.Message,
        _=>"서비스 연결 처리 중 예기치 않은 오류로 연결을 다시 시작함: "+error,
    };
    // A failing subscriber must not drop the pipe: the next State still arrives and Switch requests keep flowing.
    private void Deliver(ServiceSnapshot snapshot)
    {
        try{SnapshotReceived?.Invoke(snapshot);_lastHandlerError=null;}
        catch(Exception error)
        {
            var key=error.GetType().Name+error.Message;
            if(_lastHandlerError!=key){_lastHandlerError=key;log.Write("서비스 상태 반영 오류(연결은 유지함): "+error);}
        }
    }
    private void NotifyAvailability(bool available)
    {
        try{AvailabilityChanged?.Invoke(available);}
        catch(Exception error){log.Write($"서비스 연결 상태 반영 오류({(available?"연결":"끊김")}): "+error);}
    }
    private void LoadCursor()
    {
        if(CursorFile is not {} file)return;
        try{if(File.Exists(file))_previousRecord=JsonSerializer.Deserialize<JournalCursor>(File.ReadAllText(file));}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){log.Write("서비스 기록 위치를 읽지 못해 받은 기록을 모두 로그에 남김: "+error.Message);}
    }
    // Persisting runs off the request loop so it never sits between a Switch request and its send.
    private void NoteDelivered(string epoch,long sequence)
    {
        Volatile.Write(ref _delivered,new(epoch,sequence));
        if(CursorFile is null || Environment.TickCount64-Interlocked.Read(ref _cursorSavedAt)<1000 || Interlocked.Exchange(ref _cursorSaving,1)!=0)return;
        _=Task.Run(()=>{try{SaveCursor();}finally{Volatile.Write(ref _cursorSaving,0);}});
    }
    private void SaveCursor()
    {
        if(CursorFile is not {} file || Volatile.Read(ref _delivered) is not {} cursor)return;
        Interlocked.Exchange(ref _cursorSavedAt,Environment.TickCount64);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp=file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(cursor));File.Move(temp,file,true);_cursorError=null;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {
            if(_cursorError!=error.Message){_cursorError=error.Message;log.Write("서비스 기록 위치 저장 실패: "+error.Message);}
        }
    }
    public async ValueTask DisposeAsync()
    {
        try{await FlushConfigurationAsync();}catch(Exception error)when(error is IOException or OperationCanceledException or TimeoutException){}
        await _stop.CancelAsync();if(_loop is not null)await _loop;
        while(_requests.Reader.TryRead(out var pending))pending.Completion.TrySetCanceled();
        var until=Environment.TickCount64+1000;
        while(Volatile.Read(ref _cursorSaving)!=0 && Environment.TickCount64<until)await Task.Delay(10).ConfigureAwait(false);
        SaveCursor();
    }
    public async Task FlushConfigurationAsync()
    {
        var until=Environment.TickCount64+2000;
        while(ConfigurationPending && Connected && Environment.TickCount64<until)await Task.Delay(20).ConfigureAwait(false);
        if(ConfigurationPending)throw new IOException("서비스 설정 저장 확인 전 연결이 종료됐어.");
    }
}
