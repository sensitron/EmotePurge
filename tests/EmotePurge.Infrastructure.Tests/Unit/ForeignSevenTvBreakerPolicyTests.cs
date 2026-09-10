using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="ForeignSevenTvBreakerPolicy"/> against E4/AK 8: the two triggers stay apart (a
/// confirmed 429 opens immediately, everything else needs five consecutive failures), the open
/// duration honors <c>Retry-After</c> over the 60 s default, exactly one probe travels once the open
/// duration elapses, and a straggler from an older generation can neither close the breaker nor
/// stretch its open window. Pure and clock-controlled — no container, same shape as
/// <c>TwitchReconnectBackoffPolicyTests</c>/<c>SevenTvBackoffPolicyTests</c> in
/// <c>EmotePurge.Worker.Tests</c>.
/// </summary>
/// <remarks>
/// Every report goes through <see cref="Admit"/> first, exactly as production does: the policy hands
/// out the state a caller was admitted against, and the caller hands it back with the outcome. Tests
/// that reported an outcome without ever asking for permission would be describing a caller that
/// cannot exist.
/// </remarks>
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

        var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, retryAfter: null, Admit(policy));

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
            var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null, Admit(policy));
            Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
            Assert.True(policy.TryAcquire().Allowed);
        }

        var fifth = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null, Admit(policy));

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

        policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null, Admit(policy));
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null, Admit(policy));
        policy.RecordSuccess(Admit(policy));

        for (var i = 0; i < 4; i++)
        {
            var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null, Admit(policy));
            Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
        }

        Assert.True(policy.TryAcquire().Allowed);
    }

    [Fact]
    public void RetryAfter_OverridesTheSixtySecondDefault()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));

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

        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, retryAfter: null, Admit(policy));

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
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
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
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));
        var probe = policy.TryAcquire();
        Assert.True(probe.Allowed); // claims the probe

        var transition = policy.RecordSuccess(probe.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.Closed, transition);
        Assert.True(policy.TryAcquire().Allowed);
        // The streak really did reset: four more ordinary failures alone must not reopen it.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(
                ForeignSevenTvBreakerTransition.None,
                policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, null, Admit(policy)));
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
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));
        var probe = policy.TryAcquire();
        Assert.True(probe.Allowed); // claims the probe

        var transition = policy.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, retryAfter: null, probe.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, transition);
        Assert.False(policy.TryAcquire().Allowed);

        // And the new open window uses the default duration again — no leftover Retry-After from the
        // first opening leaks into the second.
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(policy.TryAcquire().Allowed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(policy.TryAcquire().Allowed);
    }

    /// <summary>
    /// The interleaving E4 is actually about: two lookups run side by side, the second gets a 429 and
    /// opens the breaker for the hour 7TV asked for, and only then does the first — admitted while the
    /// breaker was still closed, and knowing nothing about the incident — come back successful. Its
    /// success belongs to a state that no longer exists and must not reopen the gates: doing so would
    /// discard the Retry-After and put the feature straight back into a live lockout, which is exactly
    /// what E4 exists to prevent.
    /// </summary>
    [Fact]
    public void ALateSuccess_FromBeforeAnIntervening429_DoesNotCloseTheBreaker()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        var slowCaller = policy.TryAcquire();
        var fastCaller = policy.TryAcquire();
        Assert.True(slowCaller.Allowed);
        Assert.True(fastCaller.Allowed);

        // The fast caller finishes first, with a 429 carrying 7TV's ~1 h search-bucket reset.
        var opened = policy.RecordFailure(
            ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), fastCaller.Generation);
        Assert.Equal(ForeignSevenTvBreakerTransition.Opened, opened);

        // …and only now does the slow caller succeed.
        var lateTransition = policy.RecordSuccess(slowCaller.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, lateTransition);
        var afterwards = policy.TryAcquire();
        Assert.False(afterwards.Allowed);
        Assert.True(afterwards.OpenedByRateLimit);

        // The full Retry-After still stands — it was neither discarded nor shortened to the default.
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(policy.TryAcquire().Allowed);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(policy.TryAcquire().Allowed);
    }

    /// <summary>
    /// The same rule in the other direction: a straggler's *failure* from before the opening does not
    /// stretch the open window either. Both callers saw one incident; only the report that opened the
    /// breaker gets to say how long it lasts, or a burst of parallel failures would multiply one
    /// outage into an ever-receding one.
    /// </summary>
    [Fact]
    public void ALateFailure_FromAnOlderGeneration_DoesNotStretchTheOpenWindow()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);

        var slowCaller = policy.TryAcquire();
        var fastCaller = policy.TryAcquire();
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), fastCaller.Generation);

        clock.Advance(TimeSpan.FromSeconds(5));
        var lateTransition = policy.RecordFailure(
            ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromHours(1), slowCaller.Generation);

        Assert.Equal(ForeignSevenTvBreakerTransition.None, lateTransition);
        // Still the original 10 s window, not the straggler's hour.
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(policy.TryAcquire().Allowed);
    }

    /// <summary>
    /// A probe that never reached 7TV releases its slot, but a straggler calling the same method must
    /// not release a probe it does not own — otherwise two callers could be in flight as "the one
    /// probe".
    /// </summary>
    [Fact]
    public void ReleasingAProbe_FromAnOlderGeneration_DoesNotFreeTheCurrentProbeSlot()
    {
        var clock = NewClock();
        var policy = new ForeignSevenTvBreakerPolicy(clock.Provider);
        var straggler = policy.TryAcquire();
        policy.RecordFailure(ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromSeconds(10), Admit(policy));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(policy.TryAcquire().Allowed); // the real probe claims the slot

        policy.ReleaseProbeWithoutOutcome(straggler.Generation);

        Assert.False(policy.TryAcquire().Allowed);
    }

    // The generation a caller would be admitted against right now — production reads it off the
    // decision that admitted the call, and so does every test here.
    private static long Admit(ForeignSevenTvBreakerPolicy policy) => policy.TryAcquire().Generation;

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
