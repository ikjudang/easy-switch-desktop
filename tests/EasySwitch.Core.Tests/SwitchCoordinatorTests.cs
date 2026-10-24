using EasySwitch.Core;
using EasySwitch.Hidpp;

namespace EasySwitch.Core.Tests;

public class SwitchCoordinatorTests
{
    private static SwitchCoordinator Create() => new(_ => { }, TimeSpan.Zero, retryDelay: TimeSpan.Zero);

    [Fact]
    public async Task DifferentDeviceWritesProceedWhileTheKeyboardWriteIsBlocked()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyboard=new FakeDevice{Id="keyboard",OnWrite=async(_,cancel)=>{entered.TrySetResult();await release.Task.WaitAsync(cancel);}};
        var mouse=new FakeDevice{Id="mouse"};keyboard.Replies.Enqueue(new(3,1));mouse.Replies.Enqueue(new(3,1));
        var coordinator=Create();var first=coordinator.SwitchAsync(keyboard,1,"parallel");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Equal(SwitchOutcome.TargetReported,await coordinator.SwitchAsync(mouse,1,"parallel").WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(first.IsCompleted);
        }
        finally{release.TrySetResult();}
        Assert.Equal(SwitchOutcome.TargetReported,await first);
    }

    [Fact]
    public async Task TelemetryRetainsRetryCountsTimingsAndFinalOutcome()
    {
        var device=new FakeDevice{OnWrite=async (_,cancel)=>await Task.Delay(15,cancel)};
        device.Replies.Enqueue(new(3,0));device.Replies.Enqueue(new(3,1));
        var trace=new List<SwitchProgress>();
        Assert.Equal(SwitchOutcome.TargetReported,await Create().SwitchAsync(device,1,"test",trace.Add));
        var end=trace.Last();Assert.Equal(SwitchStep.Ended,end.Step);Assert.Equal(SwitchOutcome.TargetReported,end.Outcome);
        Assert.Equal(SwitchEndReason.TargetReported,end.Reason);Assert.Equal(2,end.Attempt);Assert.Equal(2,end.Writes);Assert.Equal(2,end.SendCalls);Assert.Equal(1,end.ReportedHost);
        Assert.True(end.WriteMs>=20);Assert.True(end.ElapsedMs>=end.WriteMs+end.VerifyMs+end.QueueMs);
        Assert.Equal(2,trace.Count(p=>p.Step==SwitchStep.WriteStarted));Assert.Equal(2,trace.Count(p=>p.Step==SwitchStep.Written));
        Assert.Equal(2,trace.Count(p=>p.Step==SwitchStep.VerifyStarted));
        Assert.Equal(trace.Select(p=>p.ElapsedMs).Order(),trace.Select(p=>p.ElapsedMs));
    }
    [Fact]
    public async Task SharedRequestReceivesEndAndObserverFailureDoesNotChangeSwitch()
    {
        var coordinator=new SwitchCoordinator(_=>{},Timeout.InfiniteTimeSpan);
        var device=new FakeDevice();var firstTrace=new List<SwitchProgress>();var joinedTrace=new List<SwitchProgress>();
        var first=coordinator.SwitchAsync(device,1,"first",p=>{firstTrace.Add(p);throw new IOException("code sink unavailable");});
        var joined=coordinator.SwitchAsync(device,1,"joined",joinedTrace.Add);
        Assert.Same(first,joined);coordinator.CancelAll("test");
        Assert.Equal(SwitchOutcome.Cancelled,await joined);
        Assert.Equal(SwitchOutcome.Cancelled,joinedTrace.Last().Outcome);Assert.Equal(1,joinedTrace.Last().Writes);
        Assert.Equal(SwitchStep.Ended,firstTrace.Last().Step);
    }
    [Fact]
    public async Task DeadlineDuringWriteIncludesSpentTimeAndDoesNotClaimWritten()
    {
        var device=new FakeDevice{OnWrite=async(_,cancel)=>await Task.Delay(Timeout.Infinite,cancel)};
        var coordinator=new SwitchCoordinator(_=>{},budget:TimeSpan.FromMilliseconds(50));var trace=new List<SwitchProgress>();
        Assert.Equal(SwitchOutcome.Unverified,await coordinator.SwitchAsync(device,1,"test",trace.Add));
        var end=trace.Last();Assert.Equal(SwitchEndReason.Deadline,end.Reason);Assert.Equal(0,end.Writes);Assert.Equal(1,end.Attempt);Assert.True(end.WriteMs>=20);
    }

    [Fact]
    public async Task DoesNotWriteForCurrentHost()
    {
        var device = new FakeDevice();
        Assert.Equal(SwitchOutcome.AlreadyCurrent, await Create().SwitchAsync(device, 0, "test"));
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task RetriesUntilDeviceReportsTarget()
    {
        var device = new FakeDevice();
        device.Replies.Enqueue(new HostInfo(3, 0));
        device.Replies.Enqueue(new HostInfo(3, 1));
        Assert.Equal(SwitchOutcome.TargetReported, await Create().SwitchAsync(device, 1, "test"));
        Assert.Equal(new[] { 1, 1 }, device.Writes);
    }

    [Fact]
    public async Task StopsAfterBoundedRetriesWhenStillOnSource()
    {
        var device = new FakeDevice();
        Assert.Equal(SwitchOutcome.StillOnSource, await Create().SwitchAsync(device, 2, "test"));
        Assert.Equal(3, device.Writes.Count);
    }

    [Fact]
    public async Task TimeoutDoesNotClaimSuccessAndStillRetries()
    {
        var device = new FakeDevice { NoReplies = true };
        Assert.Equal(SwitchOutcome.Unverified, await Create().SwitchAsync(device, 1, "test"));
        Assert.Equal(3, device.Writes.Count);
    }

    [Fact]
    public async Task DisconnectionIsNotDestinationConfirmation()
    {
        var device = new FakeDevice { DisconnectOnWrite = true };
        Assert.Equal(SwitchOutcome.LocalLinkClosed, await Create().SwitchAsync(device, 1, "test"));
        Assert.Single(device.Writes);
    }

    [Fact]
    public async Task InvalidDeviceHostDoesNotSend()
    {
        var device = new FakeDevice { HostCount = 1 };
        Assert.Equal(SwitchOutcome.Failed, await Create().SwitchAsync(device, 2, "test"));
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task NewTargetCancelsOlderRetries()
    {
        var coordinator = new SwitchCoordinator(_ => { }, Timeout.InfiniteTimeSpan);
        var device = new FakeDevice();
        var first = coordinator.SwitchAsync(device, 1, "old");
        var second = coordinator.SwitchAsync(device, 2, "new");
        Assert.Equal(SwitchOutcome.Cancelled, await first);
        await device.SecondWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.CancelAll("test finished");
        Assert.Equal(SwitchOutcome.Cancelled, await second);
        Assert.Equal(new[] { 1, 2 }, device.Writes);
    }

    [Fact]
    public async Task DuplicateSignalSharesExistingWork()
    {
        var coordinator = new SwitchCoordinator(_ => { }, Timeout.InfiniteTimeSpan);
        var device = new FakeDevice { DisconnectOnWrite = true };
        var first = coordinator.SwitchAsync(device, 1, "button");
        var second = coordinator.SwitchAsync(device, 1, "echo");
        Assert.Same(first, second);
        coordinator.CancelAll("test finished");
        await first;
        Assert.Single(device.Writes);
    }

    [Fact]
    public async Task NewIntentForCachedCurrentHostStillWritesAfterSupersedingDeparture()
    {
        var coordinator = new SwitchCoordinator(_ => { }, Timeout.InfiniteTimeSpan);
        var device = new FakeDevice();
        var departure = coordinator.SwitchAsync(device, 1, "departure");
        var back = coordinator.SwitchAsync(device, 0, "return before cache updates");
        Assert.Equal(SwitchOutcome.Cancelled, await departure);
        await device.SecondWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.CancelAll("test finished");
        await back;
        Assert.Equal(new[] { 1, 0 }, device.Writes);
    }

    [Fact]
    public async Task StopWaitsForPreviouslyCancelledWorkToFinish()
    {
        var coordinator = new SwitchCoordinator(_ => { }, Timeout.InfiniteTimeSpan);
        var device = new FakeDevice();
        var pending = coordinator.SwitchAsync(device, 1, "follow");
        coordinator.CancelAll("selection changed");
        await coordinator.StopAsync("exit");
        Assert.True(pending.IsCompleted);
        Assert.Equal(SwitchOutcome.Cancelled, await pending);
    }

    [Fact]
    public async Task KeyboardReturnCanCancelPendingMouseRetry()
    {
        var coordinator = new SwitchCoordinator(_ => { }, Timeout.InfiniteTimeSpan);
        var device = new FakeDevice();
        var work = coordinator.SwitchAsync(device, 1, "follow");
        coordinator.CancelAll("keyboard returned");
        Assert.Equal(SwitchOutcome.Cancelled, await work);
        Assert.Single(device.Writes);
    }

    [Fact]
    public async Task WirelessDeviceLeavingStopsRetriesEvenWhenReceiverRemainsOpen()
    {
        var device = new FakeDevice { DisconnectWirelessOnWrite = true, LocalLinkConnected = true };
        Assert.Equal(SwitchOutcome.LocalLinkClosed, await Create().SwitchAsync(device, 1, "Unifying/Bolt"));
        Assert.True(device.TransportAvailable);
        Assert.Single(device.Writes);
        Assert.Equal(0, device.ReadCount);
    }

    [Fact]
    public async Task KnownOfflineReceiverSlotDoesNotReceiveCommands()
    {
        var device = new FakeDevice { LocalLinkConnected = false };
        Assert.Equal(SwitchOutcome.Unverified, await Create().SwitchAsync(device, 1, "offline slot"));
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task TemporaryWriteFailureIsRetriedWithinBudget()
    {
        var device = new FakeDevice { FailedWritesRemaining = 1 };
        device.Replies.Enqueue(new HostInfo(3, 1));
        Assert.Equal(SwitchOutcome.TargetReported, await Create().SwitchAsync(device, 1, "waking mouse"));
        Assert.Equal(2, device.Writes.Count);
    }

    [Fact]
    public async Task ReturningMouseInvalidatesOldDepartureRetries()
    {
        var device = new FakeDevice();
        device.OnWrite = (_, _) => { device.ConnectionGeneration++; return Task.CompletedTask; };
        Assert.Equal(SwitchOutcome.Cancelled, await Create().SwitchAsync(device, 1, "rapid round trip"));
        Assert.Single(device.Writes);
        Assert.Equal(0, device.ReadCount);
    }

    [Fact]
    public async Task NewIntentWaitsUntilAnAlreadyStartedWriteFinishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new FakeDevice { OnWrite = (target, _) => target == 1 ? release.Task : Task.CompletedTask };
        device.Replies.Enqueue(new HostInfo(3, 2));
        var coordinator = Create();
        var old = coordinator.SwitchAsync(device, 1, "old");
        var latest = coordinator.SwitchAsync(device, 2, "latest");
        Assert.Single(device.Writes);
        release.SetResult();
        Assert.Equal(SwitchOutcome.Cancelled, await old);
        Assert.Equal(SwitchOutcome.TargetReported, await latest);
        Assert.Equal(new[] { 1, 2 }, device.Writes);
    }

    [Fact]
    public async Task VerificationHasAnOverallDeadline()
    {
        var device = new FakeDevice();
        var coordinator = new SwitchCoordinator(_ => { }, Timeout.InfiniteTimeSpan, budget: TimeSpan.FromMilliseconds(50));
        Assert.Equal(SwitchOutcome.Unverified, await coordinator.SwitchAsync(device, 1, "deadline"));
        Assert.Single(device.Writes);
    }

    [Fact]
    public async Task DepartureDuringVerificationReadStopsFurtherWrites()
    {
        var device = new FakeDevice { LocalLinkConnected = true };
        device.OnRead = () => { device.LocalLinkConnected = false; return null; };
        Assert.Equal(SwitchOutcome.LocalLinkClosed, await Create().SwitchAsync(device, 1, "late disconnect notice"));
        Assert.Single(device.Writes);
    }

    private sealed class FakeDevice : IHostDevice
    {
        public string Id {get;init;} = "test-device";
        public string Name => "Mouse";
        public bool TransportAvailable { get; private set; } = true;
        public int HostCount { get; init; } = 3;
        public int? CurrentHost => 0;
        public bool? LocalLinkConnected { get; set; }
        public long ConnectionGeneration { get; set; }
        public bool NoReplies { get; init; }
        public bool DisconnectOnWrite { get; init; }
        public bool DisconnectWirelessOnWrite { get; init; }
        public int FailedWritesRemaining { get; set; }
        public int ReadCount { get; private set; }
        public Func<int, CancellationToken, Task>? OnWrite { get; set; }
        public Func<HostInfo?>? OnRead { get; set; }
        public TaskCompletionSource SecondWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> Writes { get; } = [];
        public Queue<HostInfo> Replies { get; } = new();
        public async Task SetHostAsync(int target, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Writes.Add(target);
            if (Writes.Count == 2) SecondWrite.TrySetResult();
            if (FailedWritesRemaining-- > 0) throw new IOException("temporary transport error");
            if (DisconnectOnWrite) TransportAvailable = false;
            if (DisconnectWirelessOnWrite) LocalLinkConnected = false;
            if (OnWrite is not null) await OnWrite(target, cancel);
        }
        public Task<HostInfo?> ReadHostAsync(CancellationToken cancel)
        {
            ReadCount++;
            return Task.FromResult(OnRead is not null ? OnRead() : NoReplies ? null : Replies.Count > 0 ? Replies.Dequeue() : new HostInfo(3, 0));
        }
    }
}
