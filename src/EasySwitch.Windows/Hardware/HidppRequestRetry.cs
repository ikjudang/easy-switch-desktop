using EasySwitch.Hidpp;

namespace EasySwitch.Windows.Hardware;

internal static class HidppRequestRetry
{
    internal static async Task<byte[]?> ReadAsync(Func<Task<byte[]?>> read, Action<byte[],HidppErrorInfo> error,
        CancellationToken cancel, TimeSpan? retryDelay=null)
    {
        for(var attempt=0;attempt<3;attempt++)
        {
            var raw=await read().ConfigureAwait(false);
            if(raw is null || !HidppFrameParser.IsError(raw))return raw;
            var info=HidppFrameParser.ParseError(raw)!;error(raw,info);
            // HID++ 1.0 and 2.0 assign different values to BUSY. In particular 1.0 C09
            // (resource error) is not 2.0 C09 (unsupported), nor is it a transient BUSY.
            if(info.ErrorCode!=(info.Protocol==2?0x08:0x07))return raw;
            if(attempt==2)throw new HidppBusyException($"HID++ {info.Protocol}.0 BUSY C{info.ErrorCode:X2}");
            await Task.Delay(retryDelay??TimeSpan.FromMilliseconds(100),cancel).ConfigureAwait(false);
        }
        return null;
    }
}
internal sealed class HidppBusyException(string message):IOException(message);
