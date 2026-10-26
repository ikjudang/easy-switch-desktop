using System.IO.Pipes;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Service;

internal static class ServiceDeviceHandoff
{
    internal static async Task<DeviceInfo[]> CaptureAsync(string? pipeName=null,int? fixturePid=null)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var pipe=new NamedPipeClientStream(".",pipeName??ServiceRegistration.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        ServiceRegistration.VerifyServer(pipe,fixturePid);
        await ServiceWire.WriteAsync(pipe,new ServiceRequest(),deadline.Token).ConfigureAwait(false);
        var reply=await ServiceWire.ReadAsync<ServiceReply>(pipe,deadline.Token).ConfigureAwait(false);
        if(reply.Error is not null || reply.State is null)throw new IOException("이전 서비스의 기기 정보를 읽지 못함: "+reply.Error);
        return reply.State.Devices;
    }
    internal static async Task ImportAsync(string directory,DeviceInfo[] captured,AppLog log)
    {
        var cache=new DeviceRouteCache(directory,log);
        var imported=cache.Import(captured);await cache.FlushAsync().ConfigureAwait(false);
        log.Write($"서비스 기기 정보 인계: 복원 자료 {imported}개 추가. 새 실행에서 실제 제어 연결을 다시 확인함");
    }
}
