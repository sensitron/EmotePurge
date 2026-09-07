using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure and clock-free like ReconnectPolicy — elapsed spans are passed in. The central case is the
// one the old chat-based staleness got wrong: a healthy connection whose channels are all silent
// (every broadcaster offline overnight) must never be forced to reconnect as long as Twitch's
// server PING keeps arriving.
public class TwitchWatchdogPolicyTests
{
    private static readonly TimeSpan PastStale = TwitchWatchdogPolicy.FrameStaleThreshold + TimeSpan.FromSeconds(1);

    [Fact]
    public void Decide_ConnectedWithRecentFrame_DoesNothing_EvenAfterHoursWithoutChat()
    {
        // Frames fresh (server PING two minutes ago) — chat silence is irrelevant to the policy,
        // which never even sees a chat timestamp.
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: TimeSpan.FromHours(9),
            sinceLastFrame: TimeSpan.FromMinutes(2),
            sinceLastForcedReconnect: null);

        Assert.False(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_ConnectedWithStaleFrames_ForcesReconnect()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: TimeSpan.FromHours(1),
            sinceLastFrame: PastStale,
            sinceLastForcedReconnect: null);

        Assert.True(decision.ForceReconnect);
        Assert.NotNull(decision.Reason);
    }

    [Fact]
    public void Decide_ConnectedFrameExactlyAtThreshold_ForcesReconnect()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: TimeSpan.FromHours(1),
            sinceLastFrame: TwitchWatchdogPolicy.FrameStaleThreshold,
            sinceLastForcedReconnect: null);

        Assert.True(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_ConnectedStaleButInCooldown_DoesNothing()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: TimeSpan.FromHours(1),
            sinceLastFrame: PastStale,
            sinceLastForcedReconnect: TwitchWatchdogPolicy.FrameStaleThreshold - TimeSpan.FromMinutes(1));

        Assert.False(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_ConnectedStalePastCooldown_ForcesReconnectAgain()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: TimeSpan.FromHours(1),
            sinceLastFrame: PastStale,
            sinceLastForcedReconnect: TwitchWatchdogPolicy.FrameStaleThreshold + TimeSpan.FromSeconds(1));

        Assert.True(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_ConnectedWithoutAnyFrame_FallsBackToOpenAttempt()
    {
        // A connect that never completed the IRC handshake produces no frames — the open-attempt
        // fallback is what keeps that from being permanently undetectable.
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: PastStale,
            sinceLastFrame: null,
            sinceLastForcedReconnect: null);

        Assert.True(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_ConnectedWithNoReferenceAtAll_DoesNothing()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: true,
            sinceOpenAttempt: null,
            sinceLastFrame: null,
            sinceLastForcedReconnect: null);

        Assert.False(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_DisconnectedBeforeFirstConnectAttempt_DoesNothing()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: false,
            sinceOpenAttempt: null,
            sinceLastFrame: null,
            sinceLastForcedReconnect: null);

        Assert.False(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_Disconnected_ForcesReconnectWithoutStaleThreshold()
    {
        // Fresh frames don't shield a client that says it is down — the silence threshold exists
        // only to avoid mistaking a quiet connection for a dead one, and there is no such ambiguity
        // here.
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: false,
            sinceOpenAttempt: TimeSpan.FromMinutes(3),
            sinceLastFrame: TimeSpan.FromSeconds(10),
            sinceLastForcedReconnect: null);

        Assert.True(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_DisconnectedInCooldown_DoesNothing()
    {
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: false,
            isConnected: false,
            sinceOpenAttempt: TimeSpan.FromMinutes(3),
            sinceLastFrame: null,
            sinceLastForcedReconnect: TwitchWatchdogPolicy.DisconnectedCooldown - TimeSpan.FromSeconds(1));

        Assert.False(decision.ForceReconnect);
    }

    [Fact]
    public void Decide_ClientSpentWithFreshFrameAndWithinCooldown_StillForcesReconnect()
    {
        // A spent client (issue #114) is a wrong-object problem, not a staleness problem: neither
        // fresh frames nor the frame-stale cooldown shield it.
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: true,
            isConnected: true,
            sinceOpenAttempt: TimeSpan.FromHours(1),
            sinceLastFrame: TimeSpan.FromSeconds(5),
            sinceLastForcedReconnect: TimeSpan.FromSeconds(1));

        Assert.True(decision.ForceReconnect);
        Assert.NotNull(decision.Reason);
    }

    [Fact]
    public void Decide_ClientSpentAndDisconnectedBeforeFirstOpen_StillForcesReconnect()
    {
        // Without the clientSpent branch this would be the "nothing to watch over yet" case above —
        // a spent client must be replaced even if it never completed a single connect attempt.
        var decision = TwitchWatchdogPolicy.Decide(
            clientSpent: true,
            isConnected: false,
            sinceOpenAttempt: null,
            sinceLastFrame: null,
            sinceLastForcedReconnect: null);

        Assert.True(decision.ForceReconnect);
    }
}
