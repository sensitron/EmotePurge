using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure state, no container: the gate is two independent one-shot signals, and the point of the
// second one (issue #54) is precisely that it is *not* implied by the first — boot recovery being
// over says nothing about whether the worker is already listening for Redis commands.
public class BootRecoveryGateTests
{
    [Fact]
    public void Completed_IsNotSignalledByTheCommandChannelSubscription()
    {
        var gate = new BootRecoveryGate();

        gate.MarkCommandChannelSubscribed();

        Assert.True(gate.CommandChannelSubscribed.IsCompleted);
        Assert.False(gate.Completed.IsCompleted);
    }

    [Fact]
    public void CommandChannelSubscribed_IsNotSignalledByBootRecovery()
    {
        var gate = new BootRecoveryGate();

        gate.MarkCompleted();

        Assert.True(gate.Completed.IsCompleted);
        Assert.False(gate.CommandChannelSubscribed.IsCompleted);
    }

    [Fact]
    public async Task BothSignals_CompleteOnceEachHasBeenMarked_AndTheMarksAreIdempotent()
    {
        var gate = new BootRecoveryGate();
        var both = Task.WhenAll(gate.Completed, gate.CommandChannelSubscribed);

        gate.MarkCompleted();
        gate.MarkCompleted();
        gate.MarkCommandChannelSubscribed();
        gate.MarkCommandChannelSubscribed();

        await both.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
