using System.Collections.Concurrent;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Maps a live-stream connection id (issue #128) to the subscriber key that opened it and the
/// stream's own <c>lifetime</c> <see cref="CancellationTokenSource"/>, so
/// <c>DELETE /api/live/connections/{connectionId}</c> can end that exact stream on request — the
/// client telling the Api directly that it is done, instead of the slot release depending on a proxy
/// chain (Cloudflare -&gt; nginx) noticing the browser navigated away, which production measurement
/// showed can take 15-25 s even with the keepalive fix in place.
/// </summary>
/// <remarks>
/// <para>
/// No interface (Regel 5): this is Api-internal request-lifetime plumbing with exactly one
/// implementation, not a service wrapping an external dependency — and Core/Infrastructure are off
/// the table for this issue's measurement window regardless. It is registered as a plain DI singleton
/// purely so <c>LiveEndpoints</c> shares the one instance across every request, the same reasoning
/// <see cref="Endpoints.LiveStreamKeepaliveOptions"/> already uses; nothing here needs to be
/// substituted in a test, so a test drives this real singleton directly instead.
/// </para>
/// <para>
/// Per-process only, like the in-process Twitch-token-refresh lock: correct for today's single Api
/// replica, and would need the same kind of rework (a distributed store) as that lock if a second
/// replica ever appeared — not a limitation to solve here.
/// </para>
/// </remarks>
public sealed class LiveStreamConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _connections = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a fresh connection id for <paramref name="subscriberKey"/>, wired to remove itself
    /// the instant <paramref name="lifetime"/> is cancelled from any source — the stream's own natural
    /// end, <c>MaxConnectionLifetime</c> firing, a client abort, or <see cref="TryRelease"/> below —
    /// so a later release of the same id always finds it correctly gone instead of leaking an entry.
    /// </summary>
    public string Register(string subscriberKey, CancellationTokenSource lifetime)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _connections[connectionId] = new Entry(subscriberKey, lifetime);

        // Removes the entry synchronously as part of Cancel() finishing — for the natural end this
        // runs well before LiveEndpoints.StreamAsync's outermost finally disposes the same CTS, which
        // is what keeps TryRelease below from ever racing that Dispose() in practice (see its comment).
        lifetime.Token.Register(() => _connections.TryRemove(connectionId, out _));
        return connectionId;
    }

    /// <summary>
    /// Ends the stream behind <paramref name="connectionId"/> if — and only if — it belongs to
    /// <paramref name="subscriberKey"/>. Returns whether a stream was actually cancelled purely for
    /// this class's own tests: the endpoint that calls this (see <c>LiveEndpoints</c>) answers 204
    /// regardless of the outcome, so a caller cannot probe which connection ids exist by watching the
    /// status code.
    /// </summary>
    public bool TryRelease(string connectionId, string subscriberKey)
    {
        if (!_connections.TryGetValue(connectionId, out var entry) ||
            !string.Equals(entry.SubscriberKey, subscriberKey, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            entry.Lifetime.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The stream finished tearing down — and disposed its lifetime CTS — between the lookup
            // above and this call: functionally the same as "already ended", which this endpoint also
            // answers 204 for. Concurrent Cancel() calls on one CTS are safe by themselves; only a
            // Cancel() racing a concurrent Dispose() is not, and Register's callback above already
            // removes this very entry synchronously as part of Cancel() completing — so this catch is
            // a last-resort safeguard for an already narrow window, not the everyday path.
            return false;
        }
    }

    /// <summary>
    /// Whether an entry for <paramref name="connectionId"/> is still present, regardless of ownership.
    /// Internal and test-only (the Api project has <c>InternalsVisibleTo</c> for
    /// <c>EmotePurge.Api.Tests</c>, same as <c>LiveEndpoints.StreamAsync</c>): nothing in production
    /// code needs this, only a test verifying that a given exit path actually removed the entry — as
    /// opposed to <see cref="TryRelease"/> merely returning <see langword="false"/>, which it also does
    /// when the entry is still present but disposed-CTS-Cancel() throws, so that return value alone
    /// cannot distinguish "removed" from "leaked but currently unreleasable".
    /// </summary>
    internal bool Contains(string connectionId) => _connections.ContainsKey(connectionId);

    private sealed record Entry(string SubscriberKey, CancellationTokenSource Lifetime);
}
