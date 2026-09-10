namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>The outcome of one upstream attempt, as reported to <see cref="ForeignSevenTvBreakerPolicy"/>.</summary>
public enum ForeignSevenTvBreakerOutcome
{
    Success,

    /// <summary>A confirmed 7TV overload — HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>.</summary>
    RateLimited,

    /// <summary>Any other upstream failure (timeout, 5xx, unparseable body, …).</summary>
    OtherFailure
}

/// <summary>Whether <see cref="ForeignSevenTvBreakerPolicy.TryAcquire"/> transitioned the breaker.</summary>
public enum ForeignSevenTvBreakerTransition
{
    None,
    Opened,
    Closed
}

/// <param name="Allowed">
/// <c>false</c> means: do not call 7TV at all. <c>true</c> while closed means normal traffic;
/// <c>true</c> while the open duration has elapsed means this is the single probe request — the
/// caller must report its outcome via <see cref="ForeignSevenTvBreakerPolicy.RecordSuccess"/> or
/// <see cref="ForeignSevenTvBreakerPolicy.RecordFailure"/>.
/// </param>
/// <param name="OpenedByRateLimit">
/// Only meaningful when <paramref name="Allowed"/> is <c>false</c>: lets the caller answer a
/// rejected request with the same error code a live rate limit would have produced, rather than a
/// generic "unavailable".
/// </param>
/// <param name="RemainingOpenTime">Only meaningful when <paramref name="Allowed"/> is <c>false</c>.</param>
/// <param name="Generation">
/// The breaker state this decision was made against. It must be handed back with the outcome — see
/// the "one incident, one generation" section on <see cref="ForeignSevenTvBreakerPolicy"/> for why a
/// report from an older generation is ignored instead of applied.
/// </param>
public readonly record struct ForeignSevenTvBreakerDecision(
    bool Allowed, bool OpenedByRateLimit, TimeSpan RemainingOpenTime, long Generation);

/// <summary>
/// Circuit breaker for the foreign-channel-import preview's 7TV calls (spec 2026-09-09, E4/AK 8) —
/// handwritten and pure, no Polly, same shape as <c>TwitchReconnectBackoffPolicy</c>,
/// <c>SevenTv.SevenTvBackoffPolicy</c> and <c>TwitchWatchdogPolicy</c> in <c>EmotePurge.Worker</c>: a
/// result in, a decision out, no I/O, no logging (that is the caller's job — "the opening is logged
/// once, not per request" only holds if logging lives outside a class every rejected request calls
/// into).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two triggers, not one (E4).</b> A single confirmed rate limit — <see cref="ForeignSevenTvBreakerOutcome.RateLimited"/> —
/// opens the breaker immediately, with no failure count to accumulate first. Every other upstream
/// failure only opens it after <see cref="FailureThreshold"/> consecutive occurrences. The first
/// draft of this feature's spec got this backwards: it let four more attempts through after an
/// unambiguous 429 and re-opened on a fixed 60 s clock regardless of what 7TV actually asked for —
/// against a search-bucket lockout that runs roughly an hour
/// (<c>x-ratelimit-search-reset: 3583</c>, measured live), that would have made the feature worse
/// than the workaround it replaces.
/// </para>
/// <para>
/// <b>The open duration is not fixed.</b> A caller-supplied <c>retryAfter</c> (7TV's own
/// <c>Retry-After</c> header, or the reset hint some GraphQL error payloads carry, when present)
/// overrides <see cref="DefaultOpenDuration"/> — honoring what the provider actually asked for beats
/// guessing.
/// </para>
/// <para>
/// <b>Exactly one probe.</b> Once the open duration has elapsed, <see cref="TryAcquire"/> allows
/// through exactly one caller (the "probe") and rejects every other concurrent one until that probe
/// reports back. A failed probe re-opens the breaker (from whatever failure it reported); a
/// successful one closes it and resets the failure streak to zero.
/// </para>
/// <para>
/// <b>One incident, one generation.</b> Every decision carries the state it was made against, and a
/// report is only applied if the breaker is still in that state. Without it, two lookups running side
/// by side could undo each other: the first is admitted while the breaker is closed, the second gets
/// a 429 and opens it for the hour 7TV asked for, and then the first — admitted before that ever
/// happened — finishes successfully and unconditionally closes the breaker again, throwing away the
/// <c>Retry-After</c> and resuming traffic straight into an active lockout. Only the probe of the
/// current generation can close an open breaker; a straggler from an older one is ignored outright,
/// which also stops it from extending an open window it knows nothing about.
/// </para>
/// <para>
/// Not clock-free like the Worker policies above, because unlike a reconnect loop's single caller
/// this is consulted by concurrently arriving HTTP requests over real wall-clock time — it holds a
/// <see cref="TimeProvider"/> internally instead, the same shape <c>RateLimitTelemetryStore</c>
/// already uses, and stays substitutable in tests through the same seam.
/// </para>
/// </remarks>
public sealed class ForeignSevenTvBreakerPolicy(TimeProvider? timeProvider = null)
{
    /// <summary>Consecutive non-rate-limit failures required to open the breaker.</summary>
    public const int FailureThreshold = 5;

    /// <summary>Used when no <c>retryAfter</c> was supplied.</summary>
    public static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private bool _open;
    private bool _openedByRateLimit;
    private DateTimeOffset _openUntil;
    private bool _probeInFlight;
    private int _consecutiveFailures;

    // Bumped on every open and every close, never on a failure that merely accumulates. A report
    // carrying anything other than the current value describes a breaker state that no longer exists.
    private long _generation;

    /// <summary>
    /// Asks whether an upstream call may be made right now. Every caller that receives
    /// <c>Allowed: true</c> for a would-be probe (breaker open, open duration elapsed) is obligated
    /// to call <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> or
    /// <see cref="ReleaseProbeWithoutOutcome"/> exactly once with the outcome <i>and the decision's
    /// generation</i> — otherwise the breaker never releases <see cref="_probeInFlight"/> and stays
    /// stuck open past its own duration.
    /// </summary>
    public ForeignSevenTvBreakerDecision TryAcquire()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return new ForeignSevenTvBreakerDecision(true, false, TimeSpan.Zero, _generation);
            }

            var remaining = _openUntil - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                return new ForeignSevenTvBreakerDecision(false, _openedByRateLimit, remaining, _generation);
            }

            if (_probeInFlight)
            {
                return new ForeignSevenTvBreakerDecision(false, _openedByRateLimit, TimeSpan.Zero, _generation);
            }

            _probeInFlight = true;
            return new ForeignSevenTvBreakerDecision(true, false, TimeSpan.Zero, _generation);
        }
    }

    /// <summary>
    /// A 7TV call succeeded: closes the breaker (if open) and resets the failure streak — but only if
    /// <paramref name="generation"/> is still the current one. A success reported from an older
    /// generation is a straggler that started before the incident and proves nothing about it.
    /// </summary>
    public ForeignSevenTvBreakerTransition RecordSuccess(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return ForeignSevenTvBreakerTransition.None;
            }

            var wasOpen = _open;
            _open = false;
            _openedByRateLimit = false;
            _probeInFlight = false;
            _consecutiveFailures = 0;
            if (wasOpen)
            {
                _generation++;
                return ForeignSevenTvBreakerTransition.Closed;
            }

            return ForeignSevenTvBreakerTransition.None;
        }
    }

    /// <summary>
    /// A 7TV call failed. <paramref name="outcome"/> decides which of the two E4 triggers applies;
    /// <paramref name="retryAfter"/> is only read when the breaker actually opens as a result. A
    /// failure from an older <paramref name="generation"/> is ignored for the same reason a stale
    /// success is: the incident it belongs to has already been acted on.
    /// </summary>
    public ForeignSevenTvBreakerTransition RecordFailure(
        ForeignSevenTvBreakerOutcome outcome, TimeSpan? retryAfter, long generation)
    {
        if (outcome == ForeignSevenTvBreakerOutcome.Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "RecordFailure() kann keinen Erfolg tragen — dafür ist RecordSuccess() zuständig.");
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return ForeignSevenTvBreakerTransition.None;
            }

            _probeInFlight = false;

            if (outcome == ForeignSevenTvBreakerOutcome.RateLimited)
            {
                // No threshold to accumulate — one confirmed 429 is proof enough (E4a). Pinned at the
                // threshold rather than left untouched, so a rate limit immediately followed by an
                // ordinary failure (once the breaker reopens) does not need four more to reopen again.
                _consecutiveFailures = FailureThreshold;
                return Open(retryAfter, rateLimited: true);
            }

            _consecutiveFailures++;
            if (_open || _consecutiveFailures >= FailureThreshold)
            {
                return Open(retryAfter, rateLimited: false);
            }

            return ForeignSevenTvBreakerTransition.None;
        }
    }

    /// <summary>
    /// Releases the probe slot without reporting a health outcome — for a caller that
    /// <see cref="TryAcquire"/> let through (open duration elapsed) but that never actually reached
    /// 7TV, such as the provider-wide budget (<see cref="ForeignEmoteSetProviderBudget"/>) refusing a
    /// permit first, or an upstream answer that never touched 7TV at all (the Twitch-side failures a
    /// foreign lookup can also end in). Idempotent and safe to call unconditionally: a no-op whenever
    /// the breaker has moved on to a newer generation, or was not actually mid-probe.
    /// </summary>
    public void ReleaseProbeWithoutOutcome(long generation)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                _probeInFlight = false;
            }
        }
    }

    private ForeignSevenTvBreakerTransition Open(TimeSpan? retryAfter, bool rateLimited)
    {
        var wasOpen = _open;
        _open = true;
        _openedByRateLimit = rateLimited;
        var duration = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : DefaultOpenDuration;
        _openUntil = _timeProvider.GetUtcNow() + duration;
        _generation++;
        return wasOpen ? ForeignSevenTvBreakerTransition.None : ForeignSevenTvBreakerTransition.Opened;
    }
}
