using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="ForeignSevenTvBreakerPolicy"/> against E4/AK 8: the two triggers stay apart (a
/// confirmed 429 opens immediately, everything else needs five consecutive failures), the open
/// duration honors <c>Retry-After</c> over the 60 s default, and exactly one probe travels once the
/// open duration elapses. Pure and clock-controlled — no container, same shape as
/// <c>TwitchReconnectBackoffPolicyTests</c>/<c>SevenTvBackoffPolicyTests</c> in
/// <c>EmotePurge.Worker.Tests</c>.
/// </summary>
public class ForeignSevenTvBreakerPolicyTests
{
    [Fact]
    public void ClosedByDefault_AllowsEveryRequest()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        var decision = policy.TryAcquire();

        Assert.True(decision.Allowed);
    }

    /// <summary>E4a: no threshold to accumulate — a single confirmed 429 opens the breaker outright.</summary>
    [Fact]
    public void ConfirmedRateLimit_OpensImmediately_WithoutFiveFailures()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, retryAfter: null);

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, transition);
        var decision = policy.TryAcquire();
        Assert.False(decision.Allowed);
        Assert.True(decision.OpenedByRateLimit);
    }

    /// <summary>E4b: an ordinary failure needs the fifth consecutive occurrence, not the first.</summary>
    [Fact]
    public void OtherFailures_OnlyOpenAfterFiveConsecutive_NotBefore()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        for (var i = 0; i < 4; i++)
        {
            var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null);
            Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
            Assert.True(policy.TryAcquire().Allowed);
        }

        var fifth = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null);

        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, fifth);
        var decision = policy.TryAcquire();
        Assert.False(decision.Allowed);
        Assert.False(decision.OpenedByRateLimit);
    }

    /// <summary>A success anywhere in the streak resets it — the fifth failure after one success is
    /// only the first of a new streak, not the fifth overall.</summary>
    [Fact]
    public void ASuccess_ResetsTheConsecutiveFailureStreak()
    {
        var policy = new ForeignSevenTvBreakerPolicy(NewClock().Provider);

        policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null);
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null);
        policy.RecordSuccess();

        for (var i = 0; i < 4; i++)
        {
            var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null);
            Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
        }

        Assert.True(policy.TryAcquire().Allowed);
    }

    [Fact]
    public void RetryAfter_OverridesTheSixtySecondDefault()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(policy.TryAcquire().Allowed);

        clock.Advance(TimeSpan.FromSeconds(2)); // total 11s > the 10s Retry-After
        Assert.True(policy.TryAcquire().Allowed);
    }

    [Fact]
    public void NoRetryAfter_FallsBackToTheSixtySecondDefault()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, retryAfter: null);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(policy.TryAcquire().Allowed);

        clock.Advance(TimeSpan.FromSeconds(2)); // total 61s > the 60s default
        Assert.True(policy.TryAcquire().Allowed);
    }

    /// <summary>Once the open duration elapses, exactly one caller gets through as a probe; a
    /// concurrent second caller is still rejected until the probe reports back.</summary>
    [Fact]
    public void OnceOpenDurationElapses_ExactlyOneProbeIsAllowedThrough()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));

        var probe = policy.TryAcquire();
        var concurrentSecondCaller = policy.TryAcquire();

        Assert.True(probe.Allowed);
        Assert.False(concurrentSecondCaller.Allowed);
    }

    /// <summary>Schließen: a successful probe closes the breaker and clears the failure streak.</summary>
    [Fact]
    public void ASuccessfulProbe_ClosesTheBreaker_AndResetsTheStreak()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(policy.TryAcquire().Allowed); // claims the probe

        var transition = policy.RecordSuccess();

        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, transition);
        Assert.True(policy.TryAcquire().Allowed);
        // The streak really did reset: four more ordinary failures alone must not reopen it.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(ForeignSevenTvBreakerTransition.None, policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, null));
        }
    }

    /// <summary>
    /// Rückfall: a failed probe keeps the breaker open (with a freshly computed duration) rather than
    /// leaving it stuck half-open forever. The transition is reported as <c>None</c>, not
    /// <c>Opened</c> — the breaker was never visibly closed in between, so this is not the
    /// closed-to-open edge the "log once" contract cares about, only a continuation of the same
    /// open period under a new clock.
    /// </summary>
    [Fact]
    public void AFailedProbe_ReopensTheBreaker()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(policy.TryAcquire().Allowed); // claims the probe

        var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
        Assert.False(policy.TryAcquire().Allowed);

        // And the new open window uses the default duration again — no leftover Retry-After from the
        // first opening leaks into the second.
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(policy.TryAcquire().Allowed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(policy.TryAcquire().Allowed);
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
