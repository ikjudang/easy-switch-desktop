using EasySwitch.Core;
using EasySwitch.Hidpp;

namespace EasySwitch.Windows.Tests;

public sealed class SwitchProgressNoticeTests
{
    [Fact]
    public async Task ToastReportsCompletedWriteWhileTheCoordinatorStillWaitsForDeparture()
    {
        var device=new HeldWriteDevice();var sent=new TaskCompletionSource<DeviceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator=new SwitchCoordinator(_=>{},verificationDelay:Timeout.InfiniteTimeSpan);
        var operation=coordinator.SwitchAsync(device,1,"fixture",progress=>
        {
            if(DeviceResult.FromProgress(device.Id,device.Name,"마우스",progress) is {} row)sent.TrySetResult(row);
        });
        try
        {
            Assert.False(sent.Task.IsCompleted); // Enqueued/started is not delivered.
            device.Write.TrySetResult();
            var row=await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(operation.IsCompleted);Assert.Equal(DeviceNoticeState.CommandSent,row.State);Assert.Null(row.Indicator);Assert.Null(row.Success);
        }
        finally{coordinator.CancelAll("fixture cleanup");await operation;}
    }

    private sealed class HeldWriteDevice:IHostDevice
    {
        public TaskCompletionSource Write {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id=>"fixture-mouse";
        public string Name=>"Mouse";
        public bool TransportAvailable=>true;
        public int HostCount=>3;
        public int? CurrentHost=>0;
        public Task SetHostAsync(int target,CancellationToken cancel)=>Write.Task.WaitAsync(cancel);
        public Task<HostInfo?> ReadHostAsync(CancellationToken cancel)=>Task.FromResult<HostInfo?>(new(3,0));
    }
}
