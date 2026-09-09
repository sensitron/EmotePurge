namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// The provider-wide half of the foreign-channel-import preview's rate limiting (spec 2026-09-09,
/// E5b) — the half the per-user <c>ForeignEmoteLookup</c> ASP.NET policy cannot express at all: 7TV
/// sees one server IP, not N users, and ten different logins hitting this feature at once would
/// bypass both the cache and request coalescing entirely, each costing up to a Helix call, a
/// <c>userByConnection</c> call and ten paginated set pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent limits, one process-wide instance.</b> At most <see cref="MaxConcurrent"/>
/// foreign-set lookups may have an upstream call in flight at once, and at most
/// <see cref="MaxRequestsPerWindow"/> upstream requests may start within any rolling
/// <see cref="Window"/>. Both are consulted by <see cref="TryAcquireAsync"/> in one call: a caller
/// that clears the concurrency gate still waits out the rate window if that is the tighter
/// constraint, and vice versa.
/// </para>
/// <para>
/// <b>Waits, does not reject (E5).</b> Unlike the ASP.NET rate-limit policies elsewhere in this
/// codebase, exceeding either limit here does not produce an immediate rejection — it makes the
/// caller wait for a slot, up to the <paramref name="timeout"/> passed to
/// <see cref="TryAcquireAsync"/>. Only a caller that is still waiting when that timeout elapses gets
/// <c>null</c> back, which the hardening decorator maps to the spec's "Unavailable" outcome.
/// </para>
/// <para>
/// <b>In-process, deliberately.</b> Same limitation as the Twitch token refresh's in-process lock
/// (see <c>docs/DECISIONS.md</c>): with more than one API replica each would hold its own budget, so
/// the real ceiling would be <c>replicas × (MaxConcurrent, MaxRequestsPerWindow)</c>. Acceptable
/// today because there is exactly one replica; a second would need a distributed version of this.
/// </para>
/// <para>
/// Not a "pure" policy like <see cref="ForeignSevenTvBreakerPolicy"/> — E4 asks specifically for
/// that shape, E5 does not, and a concurrency/rate gate is inherently about coordinating real
/// waiting, not a stateless decision function.
/// </para>
/// </remarks>
public sealed class ForeignEmoteSetProviderBudget(TimeProvider? timeProvider = null) : IDisposable
{
    /// <summary>At most this many foreign-set upstream calls may be in flight at once.</summary>
    public const int MaxConcurrent = 2;

    /// <summary>At most this many upstream requests may start within any rolling <see cref="Window"/>.</summary>
    public const int MaxRequestsPerWindow = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrent, MaxConcurrent);
    private readonly Lock _windowGate = new();

    private DateTimeOffset _windowStart;
    private int _requestsInWindow;

    /// <summary>
    /// Waits for both a concurrency slot and rate-window headroom, up to <paramref name="timeout"/>
    /// in total. Returns a token that releases the concurrency slot on <see cref="IDisposable.Dispose"/>
    /// — callers must dispose it exactly once, normally in a <c>using</c> block around the guarded
    /// upstream call — or <c>null</c> if <paramref name="timeout"/> elapsed first.
    /// </summary>
    public async Task<IDisposable?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;

        if (!await _concurrency.WaitAsync(Remaining(deadline), cancellationToken))
        {
            return null;
        }

        try
        {
            while (true)
            {
                TimeSpan waitForWindow;
                lock (_windowGate)
                {
                    var now = _timeProvider.GetUtcNow();
                    if (now - _windowStart >= Window)
                    {
                        _windowStart = now;
                        _requestsInWindow = 0;
                    }

                    if (_requestsInWindow < MaxRequestsPerWindow)
                    {
                        _requestsInWindow++;
                        return new ConcurrencySlot(_concurrency);
                    }

                    waitForWindow = _windowStart + Window - now;
                }

                var remaining = Remaining(deadline);
                if (remaining <= TimeSpan.Zero)
                {
                    _concurrency.Release();
                    return null;
                }

                var delay = waitForWindow < remaining ? waitForWindow : remaining;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken);
                }
            }
        }
        catch
        {
            _concurrency.Release();
            throw;
        }
    }

    public void Dispose() => _concurrency.Dispose();

    private TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed class ConcurrencySlot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
