using System.Collections.Concurrent;

namespace EmotePurge.Infrastructure.Services;

// Serialises SyncChannelAsync across the whole process. The periodic resync worker and a JOIN
// command arriving over Redis can otherwise reconcile the same channel at the same time from two
// different AppDbContext instances, inserting the same (ChannelId, SevenTvEmoteId) rows; the loser
// gets a DbUpdateException, which in the boot path used to take the whole host down.
//
// Two key spaces, and both are needed (issue #54). The channel *name* is what a caller has in hand
// before it has touched the database, so it is the only thing the entry gate can key on. But a name
// is not the channel: during a rename handover the old and the new login denote the same
// Channel.Id, and two syncs holding the two different name gates then reconcile the very same row.
// The row gate closes that; the two are acquired in a fixed order (name, then id) so no pair of
// callers can hold one and wait for the other's.
//
// Deliberately a plain singleton class, not an interface: no external dependency, no alternative
// implementation to swap in. One SemaphoreSlim per key is kept for the process lifetime — bounded
// by the number of distinct tracked channels, so not worth evicting.
public sealed class ChannelSyncGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Entry gate, keyed on the normalized channel name. Always taken first; see the row gate for
    /// why it is not sufficient on its own.
    /// </summary>
    public Task<IDisposable> AcquireByNameAsync(string channelName, CancellationToken cancellationToken = default) =>
        AcquireAsync($"name:{channelName}", cancellationToken);

    /// <summary>
    /// Row gate, keyed on <c>Channel.Id</c>. Taken after the name gate and only once the row has
    /// actually been loaded — which is the whole point: only then is it known *which* row two
    /// differently named callers are about to reconcile.
    /// </summary>
    public Task<IDisposable> AcquireByChannelIdAsync(string channelId, CancellationToken cancellationToken = default) =>
        AcquireAsync($"id:{channelId}", cancellationToken);

    // Prefixed keys rather than two dictionaries: a Twitch login can never contain a colon, so the
    // two spaces cannot collide, and one dictionary keeps the lease type and the lifetime rule in
    // a single place.
    private async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
