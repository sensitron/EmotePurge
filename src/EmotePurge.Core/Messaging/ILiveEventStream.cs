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
