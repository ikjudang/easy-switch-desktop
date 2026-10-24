using System.IO.Pipes;
using System.Threading.Channels;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Service;

internal sealed class ServiceClient(AppLog log,string? fixturePipe=null,int? fixturePid=null):IAsyncDisposable
{
    private sealed record Pending(ServiceRequest Request,TaskCompletionSource<ServiceReply> Completion);
    private readonly Channel<Pending> _requests=Channel.CreateBounded<Pending>(256);
    private readonly CancellationTokenSource _stop=new();private Task? _loop;
    private EnginePreferences? _preferences;
    private string? _epoch,_lastError;private long _after,_powerGeneration;private volatile bool _connected;
    private volatile bool _configuring;
    public bool Connected=>_connected;
    public bool ConfigurationPending=>_configuring || Volatile.Read(ref _preferences) is not null;
    public event Action<ServiceSnapshot>? SnapshotReceived;
    public event Action<bool>? AvailabilityChanged;
    public void Start()=>_loop??=Task.Run(RunAsync);
    public void Configure(EnginePreferences preferences)=>Interlocked.Exchange(ref _preferences,preferences);
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
                    if(epoch!=reply.LogEpoch)throw new IOException("내보내기 중 서비스가 다시 시작되어 로그 파일이 바뀌었어. 다시 내보내 줘.");
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
    private async Task RunAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            Pending? current=null;
            EnginePreferences? sendingPreferences=null;
            try
            {
                await using var pipe=new NamedPipeClientStream(".",fixturePipe??ServiceRegistration.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(1000,_stop.Token);ServiceRegistration.VerifyServer(pipe,fixturePid);
                _connected=true;_lastError=null;AvailabilityChanged?.Invoke(true);
                while(!_stop.IsCancellationRequested)
                {
                    var preferences=Interlocked.Exchange(ref _preferences,null);sendingPreferences=preferences;
                    current=null;ServiceRequest request;
                    if(preferences is not null){_configuring=true;request=new("Configure",Preferences:preferences);}
                    else if(_requests.Reader.TryRead(out current))request=current.Request;
                    else request=new();
                    request=request with{Epoch=_epoch,After=_after};
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(4000);
                    await ServiceWire.WriteAsync(pipe,request,deadline.Token);
                    var reply=await ServiceWire.ReadAsync<ServiceReply>(pipe,deadline.Token);
                    if(reply.State is {} state){Interlocked.Exchange(ref _powerGeneration,state.PowerGeneration);SnapshotReceived?.Invoke(state);_epoch=state.Epoch;_after=state.Sequence;}
                    if(preferences is not null)_configuring=false;
                    sendingPreferences=null;
                    if(reply.Error is {} error){if(current is null)log.Write("서비스 요청 실패: "+error);else current.Completion.TrySetException(new IOException(error));}
                    else current?.Completion.TrySetResult(reply);
                    current=null;
                    if(request.Command=="State")
                    {
                        using var wait=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);wait.CancelAfter(100);
                        try{await _requests.Reader.WaitToReadAsync(wait.Token);}catch(OperationCanceledException)when(!_stop.IsCancellationRequested){}
                    }
                }
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                current?.Completion.TrySetException(new IOException("서비스 요청 결과를 확인하지 못했어. 자동으로 재전송하지 않아.",error));
                if(_connected){_connected=false;AvailabilityChanged?.Invoke(false);}
                if(sendingPreferences is not null)Interlocked.CompareExchange(ref _preferences,sendingPreferences,null);
                _configuring=false;
                while(_requests.Reader.TryRead(out var pending))pending.Completion.TrySetException(new IOException("서비스 연결이 끊겨 요청을 취소했어."));
                if(_stop.IsCancellationRequested)break;
                if(_lastError!=error.GetType().Name){_lastError=error.GetType().Name;log.Write("서비스 연결 재시도: "+error.Message);}
                try{await Task.Delay(500,_stop.Token);}catch(OperationCanceledException){break;}
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        try{await FlushConfigurationAsync();}catch(Exception error)when(error is IOException or OperationCanceledException or TimeoutException){}
        await _stop.CancelAsync();if(_loop is not null)await _loop;
        while(_requests.Reader.TryRead(out var pending))pending.Completion.TrySetCanceled();
    }
    public async Task FlushConfigurationAsync()
    {
        var until=Environment.TickCount64+2000;
        while(ConfigurationPending && Connected && Environment.TickCount64<until)await Task.Delay(20).ConfigureAwait(false);
        if(ConfigurationPending)throw new IOException("서비스 설정 저장 확인 전 연결이 종료됐어.");
    }
}
