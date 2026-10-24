namespace EasySwitch.Core.Tests;

public sealed class EarlySignalGateTests
{
    [Fact]
    public void FirstConnectionEventsAreReplayedInArrivalOrder()
    {
        var gate = new EarlySignalGate<string>();
        gate.Hold("battery", 1);
        gate.Hold("departure", 1);
        Assert.Equal(new[] { "battery", "departure" }, gate.Open(1).Frames);
        Assert.True(gate.IsEmpty);
    }

    [Fact]
    public void OldDepartureCannotMoveMouseAfterKeyboardHasAlreadyReturned()
    {
        var gate = new EarlySignalGate<string>();
        gate.Hold("previous departure to ch1", 1);
        gate.Hold("new departure to ch2", 2);
        var opened = gate.Open(2);
        Assert.Equal(new[] { "new departure to ch2" }, opened.Frames);
        Assert.Equal(1, opened.Discarded);
    }

    [Fact]
    public void SlowMetadataDoesNotReplayAnExpiredSwitch()
    {
        var clock = new Clock();
        var gate = new EarlySignalGate<string>(clock);
        gate.Hold("old", 1);
        clock.Milliseconds = 5000;
        gate.Hold("fresh", 1);
        var opened = gate.Open(1);
        Assert.Equal(new[] { "fresh" }, opened.Frames);
        Assert.Equal(1, opened.Discarded);
    }

    [Fact]
    public void BurstIsBoundedAndDoesNotDiscardTheLatestDeparture()
    {
        var gate = new EarlySignalGate<int>(capacity: 2);
        Assert.False(gate.Hold(1, 1));
        Assert.False(gate.Hold(2, 1));
        Assert.True(gate.Hold(3, 1));
        Assert.Equal(new[] { 2, 3 }, gate.Open(1).Frames);
    }

    [Fact]
    public void OpeningTwiceCannotDispatchTheSameEventAgain()
    {
        var gate = new EarlySignalGate<int>();
        gate.Hold(1, 1);
        Assert.Single(gate.Open(1).Frames);
        Assert.Empty(gate.Open(1).Frames);
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
