using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure state machine, no container and no clock needed — elapsed time is passed into Decide.
// Every rule under test traces back to a documented production outage (see the class comment on
// ReconnectPolicy); these tests were called for by the 2026-07-29 review (Welle D).
public class ReconnectPolicyTests
{
    [Fact]
    public void Decide_WithoutOpenInFlight_Reconnects()
    {
        var policy = new ReconnectPolicy();

        var decision = policy.Decide(openRunningFor: null);

        Assert.Equal(ReconnectAction.Reconnect, decision.Action);
    }

    [Fact]
    public void Decide_AtErrorStreakThreshold_Recreates()
    {
        var policy = new ReconnectPolicy();
        for (var i = 0; i < ReconnectPolicy.MaxConsecutiveConnectionErrors; i++)
        {
            policy.RegisterConnectionError();
        }

        var decision = policy.Decide(openRunningFor: null);

        Assert.Equal(ReconnectAction.Recreate, decision.Action);
    }

    [Fact]
    public void Decide_ErrorStreakBeatsOpenInFlight()
    {
        var policy = new ReconnectPolicy();
        for (var i = 0; i < ReconnectPolicy.MaxConsecutiveConnectionErrors; i++)
        {
            policy.RegisterConnectionError();
        }
        policy.RegisterOpenStarted();

        var decision = policy.Decide(TimeSpan.FromSeconds(5));

        Assert.Equal(ReconnectAction.Recreate, decision.Action);
    }

    [Fact]
    public void Decide_OpenInFlightBelowThreshold_Waits()
    {
        var policy = new ReconnectPolicy();
        policy.RegisterOpenStarted();

        var decision = policy.Decide(ReconnectPolicy.StuckOpenThreshold - TimeSpan.FromSeconds(1));

        Assert.Equal(ReconnectAction.Wait, decision.Action);
    }

    [Fact]
    public void Decide_OpenInFlightWithUnknownElapsed_CountsAsJustStarted()
    {
        var policy = new ReconnectPolicy();
        policy.RegisterOpenStarted();

        var decision = policy.Decide(openRunningFor: null);

        Assert.Equal(ReconnectAction.Wait, decision.Action);
    }

    [Fact]
    public void Decide_OpenInFlightAtStuckThreshold_Recreates()
    {
        var policy = new ReconnectPolicy();
        policy.RegisterOpenStarted();

        var decision = policy.Decide(ReconnectPolicy.StuckOpenThreshold);

        Assert.Equal(ReconnectAction.Recreate, decision.Action);
    }

    [Fact]
    public void RegisterConnected_ClearsErrorStreakAndInFlight()
    {
        var policy = new ReconnectPolicy();
        policy.RegisterConnectionError();
        policy.RegisterConnectionError();
        policy.RegisterOpenStarted();

        policy.RegisterConnected();

        Assert.Equal(0, policy.ConsecutiveConnectionErrors);
        Assert.False(policy.IsOpenInFlight);
        Assert.Equal(ReconnectAction.Reconnect, policy.Decide(null).Action);
    }

    [Fact]
    public void RegisterClientReplaced_StartsFromCleanState()
    {
        var policy = new ReconnectPolicy();
        for (var i = 0; i < ReconnectPolicy.MaxConsecutiveConnectionErrors; i++)
        {
            policy.RegisterConnectionError();
        }
        policy.RegisterOpenStarted();

        policy.RegisterClientReplaced();

        Assert.Equal(0, policy.ConsecutiveConnectionErrors);
        Assert.False(policy.IsOpenInFlight);
    }

    [Fact]
    public void RegisterConnectionError_ReturnsGrowingStreakAndSettlesOpen()
    {
        var policy = new ReconnectPolicy();
        policy.RegisterOpenStarted();

        Assert.Equal(1, policy.RegisterConnectionError());
        Assert.Equal(2, policy.RegisterConnectionError());
        Assert.False(policy.IsOpenInFlight);
    }
}
