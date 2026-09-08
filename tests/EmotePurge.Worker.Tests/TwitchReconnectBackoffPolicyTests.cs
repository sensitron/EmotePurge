using EmotePurge.Worker;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class TwitchReconnectBackoffPolicyTests
{
    // jitter() == 0.5 makes the multiplier exactly 1.0, so delays are deterministic.
    private static TwitchReconnectBackoffPolicy NeutralJitterPolicy() => new(() => 0.5);

    private static TwitchSessionResult Session(TimeSpan duration, TwitchSessionEndReason reason = TwitchSessionEndReason.Disconnected) =>
        new(reason, duration);

    private static TwitchSessionResult Failed(TwitchSessionEndReason reason = TwitchSessionEndReason.ConnectFailed) =>
        new(reason, SessionDuration: null);

    [Fact]
    public void NextDelay_AfterLongSession_FirstDelayIsZero()
    {
        var policy = NeutralJitterPolicy();

        var delay = policy.NextDelay(Session(TimeSpan.FromMinutes(10)));

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void NextDelay_ConsecutiveFailures_EscalateExponentiallyAndCap()
    {
        var policy = NeutralJitterPolicy();

        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(16), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay(Failed()));
    }

    [Fact]
    public void NextDelay_AtCap_JitterIsClampedNotAdded()
    {
        // Upper-band jitter (factor 1.2) on the fifth failure's raw 32 s would be 38.4 s
        // pre-clamp — the cap must apply after jitter, so the result is exactly 30 s, not 36 s.
        var policy = new TwitchReconnectBackoffPolicy(() => 1.0);

        for (var i = 0; i < 4; i++)
        {
            policy.NextDelay(Failed());
        }

        var fifth = policy.NextDelay(Failed());

        Assert.Equal(TimeSpan.FromSeconds(30), fifth);
    }

    [Fact]
    public void NextDelay_BelowCap_JitterStaysWithinTwentyPercentBand()
    {
        var low = new TwitchReconnectBackoffPolicy(() => 0.0).NextDelay(Failed());
        var high = new TwitchReconnectBackoffPolicy(() => 1.0).NextDelay(Failed());

        Assert.Equal(TimeSpan.FromSeconds(2 * 0.8), low);
        Assert.Equal(TimeSpan.FromSeconds(2 * 1.2), high);
    }

    [Fact]
    public void NextDelay_TwentyConsecutiveFailures_StayAtCap()
    {
        var policy = NeutralJitterPolicy();
        TimeSpan last = default;

        for (var i = 0; i < 20; i++)
        {
            last = policy.NextDelay(Failed());
        }

        Assert.Equal(TimeSpan.FromSeconds(30), last);
    }

    [Fact]
    public void NextDelay_LongSession_ResetsFailureStreak()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Failed());
        policy.NextDelay(Failed());
        policy.NextDelay(Failed());

        policy.NextDelay(Session(TimeSpan.FromMinutes(5)));

        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(Failed()));
    }

    [Fact]
    public void NextDelay_ShortSessions_DoNotEscalateStreak()
    {
        var policy = NeutralJitterPolicy();

        var first = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        var second = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.Zero, first);
        Assert.Equal(TimeSpan.Zero, second);
    }

    [Fact]
    public void NextDelay_ThirdAndFourthConsecutiveShortSession_HitsFlapFloor()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        var third = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        var fourth = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromSeconds(5), third);
        Assert.Equal(TimeSpan.FromSeconds(5), fourth);
    }

    [Fact]
    public void NextDelay_SessionAtSixtySeconds_LiftsDampening()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        var afterLongSession = policy.NextDelay(Session(TimeSpan.FromSeconds(60)));

        Assert.Equal(TimeSpan.Zero, afterLongSession);

        // The streak of short sessions actually reset, not just this call's own duration being long.
        var nextShortSession = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, nextShortSession);
    }

    [Fact]
    public void NextDelay_WhileDampeningActive_FloorsFailureDelays()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        var firstFailure = policy.NextDelay(Failed());
        var secondFailure = policy.NextDelay(Failed());
        var thirdFailure = policy.NextDelay(Failed());

        Assert.Equal(TimeSpan.FromSeconds(5), firstFailure);
        Assert.Equal(TimeSpan.FromSeconds(5), secondFailure);
        Assert.Equal(TimeSpan.FromSeconds(8), thirdFailure);
    }

    [Fact]
    public void NextDelay_FailureBetweenShortSessions_DoesNotCountAsSessionOrResetStreak()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        policy.NextDelay(Failed());

        var thirdShortSession = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromSeconds(5), thirdShortSession);
    }

    [Fact]
    public void NextDelay_HandshakeTimeout_CountsAsFailure()
    {
        var policy = NeutralJitterPolicy();

        var delay = policy.NextDelay(Failed(TwitchSessionEndReason.HandshakeTimeout));

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void NextDelay_UnexpectedInPlaceReconnectAfterLongSession_FloorsAtTenSeconds()
    {
        var policy = NeutralJitterPolicy();

        var delay = policy.NextDelay(Session(TimeSpan.FromMinutes(5), TwitchSessionEndReason.UnexpectedInPlaceReconnect));

        Assert.Equal(TimeSpan.FromSeconds(10), delay);
    }

    [Fact]
    public void NextDelay_FrameStaleWithLongSession_ReturnsZeroAndLiftsDampening()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        policy.NextDelay(Session(TimeSpan.FromSeconds(10)));

        var delay = policy.NextDelay(Session(TimeSpan.FromMinutes(20), TwitchSessionEndReason.FrameStale));

        Assert.Equal(TimeSpan.Zero, delay);

        var nextShortSession = policy.NextDelay(Session(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, nextShortSession);
    }
}
