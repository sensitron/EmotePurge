using System.Collections.Concurrent;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Single-flight coalescing for the foreign-channel-import preview (spec 2026-09-09, section 6):
/// concurrent lookups for the same normalized channel share one upstream call chain instead of each
/// running it themselves. Keyed on the channel name alone, regardless of <c>refresh</c> — a
/// cache-miss lookup arriving while a forced refresh for the same channel is already in flight can
/// safely share that refresh's outcome, and vice versa; both end up running the exact same chain.
/// </summary>
/// <remarks>
/// Process-wide singleton by design, like <c>ChannelSyncGate</c> — no external dependency, no
/// alternative implementation to swap in, so no interface either. The dictionary is bounded by the
/// number of channels with a lookup genuinely in flight at once, which given the provider-wide
/// concurrency budget (<see cref="ForeignEmoteSetProviderBudget.MaxConcurrent"/>) is tiny.
/// </remarks>
public sealed class ForeignEmoteSetRequestCoalescer
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ForeignEmoteSetLookupResult>>> _inFlight = new(StringComparer.Ordinal);

    public Task<ForeignEmoteSetLookupResult> CoalesceAsync(
        string normalizedChannelName, Func<Task<ForeignEmoteSetLookupResult>> upstream)
    {
        // Lazy<T>'s default thread-safety mode means the factory below can run at most once even if
        // GetOrAdd races and constructs more than one Lazy instance for the same key — only the one
        // ConcurrentDictionary actually stores ever has its Value observed, and constructing a Lazy
        // does not itself invoke the delegate it wraps, so a discarded race loser costs nothing.
        var entry = _inFlight.GetOrAdd(
            normalizedChannelName,
            key => new Lazy<Task<ForeignEmoteSetLookupResult>>(() => RunAsync(key, upstream)));

        return entry.Value;
    }

    private async Task<ForeignEmoteSetLookupResult> RunAsync(
        string normalizedChannelName, Func<Task<ForeignEmoteSetLookupResult>> upstream)
    {
        try
        {
            return await upstream();
        }
        finally
        {
            // Removed once finished, successfully or not — a permanently cached "in flight" entry
            // would freeze every later lookup for this channel on today's answer.
            _inFlight.TryRemove(normalizedChannelName, out _);
        }
    }
}
