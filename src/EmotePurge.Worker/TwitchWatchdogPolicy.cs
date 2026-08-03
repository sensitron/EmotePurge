namespace EmotePurge.Worker;

public readonly record struct WatchdogDecision(bool ForceReconnect, string? Reason);

/// <summary>
/// The staleness decision of <see cref="TwitchConnectionWatchdog"/>, separated from the transport
/// and the clock — same pattern as <see cref="ReconnectPolicy"/>, elapsed time is passed in.
/// <para>
/// Until 2026-08-03 the watchdog measured chat activity (last chat message), which is a proxy for
/// connection health that fails exactly at night: with every joined channel offline it forced a
/// reconnect of a perfectly healthy connection every ~5–10 minutes. It now measures received IRC
/// frames instead — Twitch's server PING arrives roughly every five minutes even on a completely
/// silent connection, so a healthy socket can no longer look stale (see the DECISIONS entry).
/// </para>
/// </summary>
public static class TwitchWatchdogPolicy
{
    /// <summary>
    /// Three times Twitch's ~5-minute server-PING cadence: one missed PING is jitter, three in a
    /// row means nothing has arrived on the socket for a quarter of an hour — dead by any measure.
    /// </summary>
    public static readonly TimeSpan FrameStaleThreshold = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A client that reports itself as disconnected needs no silence threshold at all: there is
    /// nothing to mistake for a quiet connection, and reconnects cannot look abusive to Twitch
    /// while no connection is up. Only a short cooldown, so a hard outage doesn't turn into a
    /// tight loop.
    /// </summary>
    public static readonly TimeSpan DisconnectedCooldown = TimeSpan.FromMinutes(1);

    /// <param name="sinceOpenAttempt">
    /// Elapsed since the last connect/reconnect attempt started, or <c>null</c> if none was ever
    /// made — in which case there is nothing to watch over yet.
    /// </param>
    /// <param name="sinceLastFrame">
    /// Elapsed since the last received IRC frame, or <c>null</c> if none has arrived yet. Falls
    /// back to <paramref name="sinceOpenAttempt"/>: a connection that never produced a single
    /// frame must still become stale eventually, or a worker whose connect never completes the
    /// handshake would be permanently undetectable.
    /// </param>
    /// <param name="sinceLastForcedReconnect">
    /// Elapsed since the watchdog last forced a reconnect, or <c>null</c> if it never has. The
    /// cooldown against re-triggering every tick while a forced reconnect has not (yet) produced
    /// frames — the 2026-07-26 reconnect storm, kept from the previous design.
    /// </param>
    public static WatchdogDecision Decide(
        bool isConnected,
        TimeSpan? sinceOpenAttempt,
        TimeSpan? sinceLastFrame,
        TimeSpan? sinceLastForcedReconnect)
    {
        if (!isConnected)
        {
            if (sinceOpenAttempt is null || IsInCooldown(sinceLastForcedReconnect, DisconnectedCooldown))
            {
                return new WatchdogDecision(false, null);
            }

            return new WatchdogDecision(true, "TwitchClient meldet sich als getrennt.");
        }

        var idleFor = sinceLastFrame ?? sinceOpenAttempt;
        if (idleFor is null
            || idleFor < FrameStaleThreshold
            || IsInCooldown(sinceLastForcedReconnect, FrameStaleThreshold))
        {
            return new WatchdogDecision(false, null);
        }

        return new WatchdogDecision(
            true,
            $"Kein IRC-Frame seit {(int)idleFor.Value.TotalSeconds}s empfangen (Schwelle {(int)FrameStaleThreshold.TotalSeconds}s) — auch Twitchs Server-PING bleibt aus.");
    }

    private static bool IsInCooldown(TimeSpan? sinceLastForcedReconnect, TimeSpan cooldown) =>
        sinceLastForcedReconnect is { } elapsed && elapsed < cooldown;
}
