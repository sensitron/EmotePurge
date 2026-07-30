using EmotePurge.Worker.SevenTv;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class SevenTvBackoffPolicyTests
{
    // jitter() == 0.5 makes the multiplier exactly 1.0, so delays are deterministic.
    private static SevenTvBackoffPolicy NeutralJitterPolicy() => new(() => 0.5);

    private static SevenTvSessionResult Failed(SevenTvSessionEndReason reason = SevenTvSessionEndReason.ConnectFailed) =>
        new(SessionEstablished: false, reason);

    [Fact]
    public void NextDelay_EscalatesExponentiallyAndCaps()
    {
        var policy = NeutralJitterPolicy();

        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(16), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(32), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay(Failed()));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay(Failed()));
    }

    [Fact]
    public void NextDelay_ServerRequestedReconnect_IsRoutineOneSecond()
    {
        // The documented ~1h TTL disconnect is the normal case — it must neither inherit nor build
        // up any backoff. This is the single most important guarantee of this class.
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Failed());
        policy.NextDelay(Failed());

        var delay = policy.NextDelay(new SevenTvSessionResult(
            SessionEstablished: true, SevenTvSessionEndReason.ServerRequestedReconnect));

        Assert.Equal(TimeSpan.FromSeconds(1), delay);

        // And the streak is gone: the next failure starts the escalation from the base again.
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(Failed()));
    }

    [Fact]
    public void NextDelay_EstablishedSession_ResetsStreak()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Failed());
        policy.NextDelay(Failed());
        policy.NextDelay(Failed());

        var delay = policy.NextDelay(new SevenTvSessionResult(
            SessionEstablished: true, SevenTvSessionEndReason.RemoteClosed));

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void NextDelay_AlreadySubscribed_HasSixtySecondFloor()
    {
        var policy = NeutralJitterPolicy();

        var delay = policy.NextDelay(new SevenTvSessionResult(
            SessionEstablished: true, SevenTvSessionEndReason.AlreadySubscribed, CloseCode: 4009));

        Assert.True(delay >= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void NextDelay_JitterStaysWithinTwentyPercentBand()
    {
        var low = new SevenTvBackoffPolicy(() => 0.0).NextDelay(Failed());
        var high = new SevenTvBackoffPolicy(() => 1.0).NextDelay(Failed());

        Assert.Equal(TimeSpan.FromSeconds(2 * 0.8), low);
        Assert.Equal(TimeSpan.FromSeconds(2 * 1.2), high);
    }

    [Fact]
    public void Reset_ClearsTheStreak()
    {
        var policy = NeutralJitterPolicy();
        policy.NextDelay(Failed());
        policy.NextDelay(Failed());

        policy.Reset();

        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(Failed()));
    }
}
