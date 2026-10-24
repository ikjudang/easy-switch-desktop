namespace EasySwitch.Windows.Hardware;

internal interface IDeviceTransport
{
    string Key {get;}
    string Name {get;}
    string ConnectionLabel {get;}
    bool IsReceiver {get;}
    bool IsOpen {get;}
    bool RecoveryRequired=>false;
    int SoftwareId {get;}
    Task<byte[]?> RequestAsync(byte[] request,CancellationToken cancel);
    Task SendAsync(byte[] request,CancellationToken cancel);
}
