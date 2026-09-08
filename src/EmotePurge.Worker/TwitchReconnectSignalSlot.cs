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
    /// </summary>
    public async Task<TwitchReconnectRequest> TakeAsync(CancellationToken ct)
    {
        await _available.WaitAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            var request = _pending!;
            _pending = null;
            _condemnedGeneration = Math.Max(_condemnedGeneration, request.ClientGeneration);
            return request;
        }
    }
}
