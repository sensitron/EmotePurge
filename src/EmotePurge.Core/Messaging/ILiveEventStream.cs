namespace EmotePurge.Core.Messaging;

/// <summary>
/// Fan-out of the <see cref="LiveEvents.Channel"/> Redis channel to the connections of one process.
/// Deliberately ASP.NET-free: the SSE framing lives entirely in the Api layer, this only produces
/// events.
/// </summary>
public interface ILiveEventStream
{
    /// <summary>
    /// Opens one subscription, or returns <c>null</c> when a connection limit is exhausted.
    /// <para>
    /// A nullable <see cref="Task{T}"/> rather than a lazily-failing enumerable on purpose: the
    /// caller must be able to answer 503 <em>before</em> the first response byte goes out, and once
    /// an SSE body has started there is no status code left to send.
    /// </para>
    /// </summary>
    /// <param name="subscriberKey">Identity the per-subscriber limit is counted against.</param>
    /// <param name="filter">Runs on the Redis handler thread; must be cheap and must not block.</param>
    Task<ILiveEventSubscription?> SubscribeAsync(
        string subscriberKey,
        Func<LiveEvent, bool> filter,
        CancellationToken cancellationToken = default);
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
