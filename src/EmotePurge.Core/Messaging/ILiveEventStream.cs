namespace EmotePurge.Core.Messaging;

/// <summary>
/// Fan-out of the <see cref="LiveEvents.Channel"/> Redis channel to the connections of one process.
/// Deliberately ASP.NET-free: the SSE framing lives entirely in the Api layer, this only produces
/// events.
/// </summary>
public interface ILiveEventStream
{
    /// <summary>
    /// Opens one subscription, or reports why none could be opened.
    /// <para>
    /// A result rather than a lazily-failing enumerable on purpose: the caller must be able to answer
    /// with an error status <em>before</em> the first response byte goes out, and once an SSE body
    /// has started there is no status code left to send.
    /// </para>
    /// </summary>
    /// <param name="subscriberKey">Identity the per-subscriber limit is counted against.</param>
    /// <param name="filter">Runs on the Redis handler thread; must be cheap and must not block.</param>
    Task<LiveEventSubscribeResult> SubscribeAsync(
        string subscriberKey,
        Func<LiveEvent, bool> filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How much of the connection budget <paramref name="subscriberKey"/> is holding right now, and
    /// what the two ceilings are. Answers the one question a browser cannot answer for itself:
    /// <c>EventSource.onerror</c> exposes neither status code nor body, so a tab that was refused
    /// knows only that its stream is closed — never whether that was a 503 (infrastructure) or a 429
    /// (budget full), which is the difference between "wait" and "close a tab" (issue #42, stage 2).
    /// <para>
    /// A snapshot, not a reservation: a slot may free or fill between this call and the next
    /// subscribe. That is acceptable because the only consumer is a hint, never a gate — every
    /// enforcement decision stays inside <see cref="SubscribeAsync"/>, where it is made under a lock.
    /// </para>
    /// </summary>
    LiveStreamQuota GetQuota(string subscriberKey);
}

/// <summary>
/// A snapshot of the live-stream connection budget as one subscriber sees it.
/// </summary>
/// <param name="OpenConnections">Streams this subscriber currently holds, across all tabs and devices.</param>
/// <param name="MaxPerSubscriber">The per-login ceiling those are counted against.</param>
/// <param name="ProcessLimitReached">
/// Whether the process-wide ceiling is exhausted as well. Reported separately from the per-login
/// count although <see cref="LiveEventSubscribeStatus.QuotaExhausted"/> collapses the two, and for a
/// reason that only appears once there is a human on the other end: "close a few tabs" is sound
/// advice for a full per-login budget and outright wrong for a full process — the tabs are then
/// someone else's. A consumer that cannot act on the difference should keep collapsing them.
/// </param>
public record LiveStreamQuota(int OpenConnections, int MaxPerSubscriber, bool ProcessLimitReached)
{
    /// <summary>
    /// Whether this subscriber's own budget is full — the condition that makes a refused stream the
    /// user's own doing, and the only one worth telling them about.
    /// </summary>
    public bool PerSubscriberLimitReached => OpenConnections >= MaxPerSubscriber;
}

/// <summary>
/// Why <see cref="ILiveEventStream.SubscribeAsync"/> could not hand out a subscription — issue #42.
/// "Redis is unreachable" and "the connection budget is full" used to collapse onto the same
/// <c>null</c>, which made <c>LiveEndpoints.OpenAsync</c> answer the same blank 503 for both and left
/// an operator unable to tell an infrastructure outage from an exhausted quota. The two connection
/// limits (process-wide <c>MaxSubscriptions</c>, per-login <c>MaxPerSubscriber</c>) share
/// <see cref="QuotaExhausted"/> rather than getting a member each: both mean the identical thing to a
/// caller — no slot right now, try again shortly — and the log lines inside
/// <c>RedisLiveEventStream</c> already carry the finer distinction for an operator reading logs.
/// Same shape as <c>SevenTvLookupStatus</c> from #32/#37.
/// </summary>
public enum LiveEventSubscribeStatus
{
    Ok,
    InfrastructureUnavailable,
    QuotaExhausted
}

/// <summary>
/// Outcome of <see cref="ILiveEventStream.SubscribeAsync"/>. Subscription is non-null if and only if
/// Status is Ok; the two factories are the only supported way to build one, so that invariant cannot
/// be broken at a call site — mirrors <c>SevenTvChannelStateResult</c>.
/// </summary>
public record LiveEventSubscribeResult(LiveEventSubscribeStatus Status, ILiveEventSubscription? Subscription)
{
    public static LiveEventSubscribeResult Ok(ILiveEventSubscription subscription) =>
        new(LiveEventSubscribeStatus.Ok, subscription);

    public static LiveEventSubscribeResult Failed(LiveEventSubscribeStatus status) =>
        new(status, null);
}

/// <summary>
/// One open subscription. Disposing it detaches it from the fan-out and completes
/// <see cref="Events"/>; the enumeration also ends when the token passed to
/// <c>Events.WithCancellation(...)</c> fires.
/// </summary>
public interface ILiveEventSubscription : IAsyncDisposable
{
    /// <summary>
    /// The events this connection is entitled to, interleaved with <see cref="LiveEvents.Ping"/>
    /// heartbeats whenever nothing else arrives. Single-consumer: enumerate it exactly once.
    /// </summary>
    IAsyncEnumerable<LiveEvent> Events { get; }
}
