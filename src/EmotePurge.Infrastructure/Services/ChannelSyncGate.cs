using System.Collections.Concurrent;

namespace EmotePurge.Infrastructure.Services;

// Serialises SyncChannelAsync per channel across the whole process. The periodic resync worker and
// a JOIN command arriving over Redis can otherwise reconcile the same channel at the same time from
// two different AppDbContext instances, inserting the same (ChannelId, SevenTvEmoteId) rows; the
// loser gets a DbUpdateException, which in the boot path used to take the whole host down.
//
// Deliberately a plain singleton class, not an interface: no external dependency, no alternative
// implementation to swap in. One SemaphoreSlim per channel name is kept for the process lifetime —
// bounded by the number of distinct tracked channels, so not worth evicting.
public sealed class ChannelSyncGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(channelName, _ => new SemaphoreSlim(1, 1));
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
