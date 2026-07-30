using System.Collections.Concurrent;

namespace EmotePurge.Infrastructure.Services;

// Serialises Twitch token refreshes per user across the whole process ("refresh the access token
// in one thread" is Twitch's own recommendation): two parallel requests hitting an expired token
// would otherwise both spend a refresh call, and with token rotation the loser could persist an
// already-superseded refresh token. Also keeps the per-user "last validated" timestamp for the
// hourly /oauth2/validate obligation — in-memory on purpose: after a restart the worst case is
// one validate call too many, which is cheaper than another DB column.
//
// Deliberately a plain singleton class, not an interface (same reasoning as ChannelSyncGate):
// no external dependency, no alternative implementation to swap in. Entries are kept for the
// process lifetime — bounded by the number of distinct logged-in users.
public sealed class TwitchTokenRefreshGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastValidatedUtc = new();

    public async Task<IDisposable> AcquireAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(twitchUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    public bool NeedsValidation(string twitchUserId, TimeSpan maxAge)
    {
        return !_lastValidatedUtc.TryGetValue(twitchUserId, out var lastValidated)
            || DateTime.UtcNow - lastValidated >= maxAge;
    }

    public void MarkValidated(string twitchUserId)
    {
        _lastValidatedUtc[twitchUserId] = DateTime.UtcNow;
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
