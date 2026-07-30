namespace EmotePurge.Worker;

public enum ReconnectAction
{
    /// <summary>Reconnect the existing client.</summary>
    Reconnect,

    /// <summary>Throw the client object away and build a new one.</summary>
    Recreate,

    /// <summary>Do nothing — an attempt is already running and has not used up its patience yet.</summary>
    Wait
}

public readonly record struct ReconnectDecision(ReconnectAction Action, string Reason);

/// <summary>
/// The reconnect decision of <see cref="TwitchChatManager"/>, separated from the transport. Every rule
/// in here comes from a production outage (2026-07-26 twice, 2026-07-27), and the whole thing used to
/// be validated by nothing but the next outage — it sat inside the project's longest file, tangled up
/// with TwitchLib event wiring, so there was no way to test it. This class has no TwitchLib dependency
/// and no clock: elapsed time is passed in.
/// <para>
/// The counters are updated from TwitchLib event handlers on arbitrary threads, hence the interlocked
/// access. <see cref="Decide"/> itself reads a consistent-enough snapshot; the caller already
/// serializes decisions behind a semaphore.
/// </para>
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>
    /// Live observed 2026-07-26: after a "Fatal network error" the underlying TwitchLib socket stayed
    /// permanently broken and ReconnectAsync() on the same client object never recovered (&gt;45 min
    /// outage). Replacing the whole client is the escape hatch. The root cause of that specific outage
    /// is fixed by the explicit reconnection policy in TwitchChatManager.CreateClient; this threshold
    /// remains as a safety net for any other way a client object can end up wedged.
    /// </summary>
    public const int MaxConsecutiveConnectionErrors = 3;

    /// <summary>
    /// If an open loop has been running this long without ever reaching a connected state, the client
    /// object is assumed wedged and gets replaced rather than waited on any further.
    /// </summary>
    public static readonly TimeSpan StuckOpenThreshold = TimeSpan.FromMinutes(10);

    private int _consecutiveConnectionErrors;
    private int _openInFlight;

    public int ConsecutiveConnectionErrors => Volatile.Read(ref _consecutiveConnectionErrors);

    public bool IsOpenInFlight => Volatile.Read(ref _openInFlight) == 1;

    /// <summary>A connect or reconnect attempt has been started.</summary>
    public void RegisterOpenStarted() => Interlocked.Exchange(ref _openInFlight, 1);

    /// <summary>
    /// An attempt finished, one way or the other, without necessarily succeeding — e.g. a connect that
    /// returned false, or an abandoned background attempt that eventually settled.
    /// </summary>
    public void RegisterOpenSettled() => Interlocked.Exchange(ref _openInFlight, 0);

    /// <summary>A connection is up: clears both the in-flight marker and the error streak.</summary>
    public void RegisterConnected()
    {
        Interlocked.Exchange(ref _openInFlight, 0);
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);
    }

    /// <summary>Returns the new streak length, for logging how close a recreate is.</summary>
    public int RegisterConnectionError()
    {
        Interlocked.Exchange(ref _openInFlight, 0);
        return Interlocked.Increment(ref _consecutiveConnectionErrors);
    }

    /// <summary>A brand-new client object: no errors against it, nothing in flight.</summary>
    public void RegisterClientReplaced()
    {
        Interlocked.Exchange(ref _openInFlight, 0);
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);
    }

    /// <param name="openRunningFor">
    /// How long the in-flight attempt has been running, or <c>null</c> if that is unknown. Only
    /// consulted when an attempt is actually in flight. Passed in rather than measured here so the
    /// policy needs no clock.
    /// </param>
    public ReconnectDecision Decide(TimeSpan? openRunningFor)
    {
        var errors = ConsecutiveConnectionErrors;
        if (errors >= MaxConsecutiveConnectionErrors)
        {
            return new ReconnectDecision(
                ReconnectAction.Recreate,
                $"{errors} aufeinanderfolgende Verbindungsfehler erreicht (Schwelle {MaxConsecutiveConnectionErrors}).");
        }

        if (!IsOpenInFlight)
        {
            return new ReconnectDecision(ReconnectAction.Reconnect, "Kein Verbindungsaufbau aktiv.");
        }

        // An open loop can now take arbitrarily long (the reconnection policy retries indefinitely),
        // while the watchdog ticks every minute — so an attempt in progress is normally left alone.
        // Unknown elapsed time counts as "just started" for the same reason: escalating to a recreate
        // on no evidence would throw away a healthy attempt.
        var runningFor = openRunningFor ?? TimeSpan.Zero;
        if (runningFor < StuckOpenThreshold)
        {
            return new ReconnectDecision(
                ReconnectAction.Wait,
                $"Verbindungsaufbau läuft seit {(int)runningFor.TotalSeconds}s noch — kein zusätzlicher Reconnect.");
        }

        return new ReconnectDecision(
            ReconnectAction.Recreate,
            $"Verbindungsaufbau hängt seit {(int)runningFor.TotalMinutes} Minuten ohne Erfolg.");
    }
}
