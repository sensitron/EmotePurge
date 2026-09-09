namespace EmotePurge.Worker;

/// <summary>
/// What offering a signal did to the slot. The slot only reports this; logging it is the manager's
/// job (plan 2.3: "der Manager loggt").
/// </summary>
public enum TwitchReconnectSignalOutcome
{
    /// <summary>The slot was empty; the request now sits in it.</summary>
    Deposited,

    /// <summary>The slot already held a signal; only its reason and detail were replaced.</summary>
    Coalesced,

    /// <summary>The request's client generation is already condemned; it was dropped, not queued.</summary>
    Discarded
}

/// <summary>
/// A latching, capacity-one mailbox for "the connection needs rebuilding", clock-free and free of
/// any TwitchLib type — the state <see cref="TwitchConnectionWatchdog"/>'s rebuild loop waits on,
/// and TwitchLib event handlers offer into.
/// <para>
/// <b>Why offering needs a generation, not just a full/empty slot.</b> A socket failure delivers up
/// to two <c>OnDisconnected</c> calls plus one <c>OnConnectionError</c> within about 2.5 s of each
/// other, all from the same, now-dead client. Trace the sequence that breaks without this rule:
/// (1) the loop takes the first of those signals out of the slot — the slot is now empty and the
/// generation of that client stands condemned; (2) the loop's delay for this attempt is not 0 s but
/// a floor (the flap floor, 5 s, or the tripwire floor, 10 s) — the old client is still wired for
/// that long; (3) the old client's <c>OnConnectionError</c> lands during that wait and, without a
/// generation check, would simply refill the now-empty slot; (4) the loop finishes a *successful*
/// rebuild and rejoin on the *new* client; (5) it then goes back to waiting, sees the stale signal
/// sitting in the slot from step 3, and tears the just-repaired connection down again for a loss
/// that already happened. Condemning a generation on take, and discarding any later offer for that
/// generation or an older one, closes exactly that gap — a late echo of a client already replaced
/// can no longer masquerade as a new loss.
/// </para>
/// <para>
/// <b>Two places condemn, not one.</b> Taking a signal condemns its generation, and so does
/// retiring a client (<see cref="CondemnGeneration"/>) — because an attempt can fail without its
/// signal ever having been taken, and its client stays wired for the whole backoff. Condemning
/// only on take left exactly that generation able to deposit a loss that outlived it.
/// </para>
/// </summary>
public sealed class TwitchReconnectSignalSlot
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private TwitchReconnectRequest? _pending;
    private int _condemnedGeneration;

    /// <summary>
    /// Offers a signal. Generation is checked before slot occupancy: a request for an already
    /// condemned generation is discarded even while the slot happens to be full.
    /// </summary>
    public TwitchReconnectSignalOutcome Offer(TwitchReconnectRequest request)
    {
        lock (_gate)
        {
            if (request.ClientGeneration <= _condemnedGeneration)
            {
                return TwitchReconnectSignalOutcome.Discarded;
            }

            if (_pending is null)
            {
                _pending = request;
                _available.Release();
                return TwitchReconnectSignalOutcome.Deposited;
            }

            _pending = _pending with { Reason = request.Reason, Detail = request.Detail };
            return TwitchReconnectSignalOutcome.Coalesced;
        }
    }

    /// <summary>
    /// Waits for a signal, then in one step under the same lock: returns it, empties the slot, and
    /// condemns its generation — so a handler running on the reader thread cannot slip an offer in
    /// between the emptying and the condemning.
    /// <para>
    /// The loop around the wait is not padding. <see cref="CondemnGeneration"/> may drop a signal
    /// that is already lying in the slot and take its wake-up token back with it, and it can lose
    /// that race against a taker which has just passed the wait but not yet reached the lock. Such
    /// a taker finds the slot empty and goes back to waiting — which is exactly right: the rebuild
    /// that signal asked for is the one currently running.
    /// </para>
    /// </summary>
    public async Task<TwitchReconnectRequest> TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            await _available.WaitAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (_pending is not { } request)
                {
                    continue;
                }

                _pending = null;
                _condemnedGeneration = Math.Max(_condemnedGeneration, request.ClientGeneration);
                return request;
            }
        }
    }

    /// <summary>
    /// Condemns <paramref name="generation"/> without taking anything out of the slot, and drops a
    /// signal already deposited for it (or for an older one).
    /// <para>
    /// The manager calls this where a client is detached and replaced — for <b>every</b> attempt,
    /// failed or not. Condemning only in <see cref="TakeAsync"/> left the generation of a *failed*
    /// attempt un-condemned, and that is the same collapse as above through a different door:
    /// (1) the signal of generation 1 is taken, generation 1 is condemned; (2) generation 2 runs
    /// into the 10 s handshake timeout but stays current and wired for the whole backoff; (3) its
    /// "004" arrives late, passes the identity check and starts its session, so the handshake
    /// guard stops discarding its events; (4) generation 2 drops during the backoff and its signal
    /// is *accepted*, because generation 2 was never condemned; (5) the loop builds generation 3,
    /// connects and rejoins; (6) its next wait finds the stale signal of generation 2 and tears
    /// the healthy connection down. Dropping an already-deposited signal is part of the fix, not a
    /// nicety — leaving it in place produces step 6 regardless. It costs nothing: a signal from a
    /// client we are replacing right now asks for a rebuild that is already under way.
    /// </para>
    /// </summary>
    public void CondemnGeneration(int generation)
    {
        lock (_gate)
        {
            _condemnedGeneration = Math.Max(_condemnedGeneration, generation);

            if (_pending is null || _pending.ClientGeneration > _condemnedGeneration)
            {
                return;
            }

            _pending = null;

            // The wake-up token goes with it. The semaphore's maximum count is one, so leaving the
            // token behind would make the next Offer's Release() throw rather than deposit — and a
            // taker woken by it would find an empty slot anyway. Wait(0) never blocks; it returns
            // false when a taker has already consumed the token, which is the race TakeAsync's
            // loop covers.
            _available.Wait(0);
        }
    }
}
