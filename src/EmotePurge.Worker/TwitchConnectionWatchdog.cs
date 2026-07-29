namespace EmotePurge.Worker;

// Erkennt stille Verbindungsabbrüche, bei denen TwitchLib selbst kein OnDisconnected feuert
// (live beobachtet: ~6 Minuten Stillstand ohne jedes Event, s. Projekt-Notizen 2026-07-24/25).
public class TwitchConnectionWatchdog(
    ILogger<TwitchConnectionWatchdog> logger,
    ITwitchChatManager twitchChatManager) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    // Großzügig über dem beobachteten ~6-Minuten-Fall, um ein schlicht ruhiges Channel nicht
    // fälschlich als Freeze zu werten — bekannte Grenze bei Channels mit wirklich seltener Chat-Aktivität.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    // A client that reports itself as disconnected needs no silence threshold at all: there is
    // nothing to mistake for a quiet channel, and reconnects cannot look abusive to Twitch while
    // no connection is up. Only a short cooldown, so a hard outage doesn't turn into a tight loop.
    private static readonly TimeSpan DisconnectedCooldown = TimeSpan.FromMinutes(1);

    // Live beobachtet 2026-07-26: Ein erzwungener Reconnect aktualisiert LastMessageReceivedUtc nicht
    // (das passiert nur bei einer tatsächlich empfangenen Chat-Nachricht) — auf Channels ohne Chat
    // (z. B. weil der Broadcaster offline ist) feuerte CheckOnceAsync dadurch bei JEDEM Tick erneut,
    // also im 60-Sekunden-Takt auf unbegrenzte Zeit, statt nur einmalig. Dieser Reconnect-Sturm gegen
    // Twitch IRC ist der wahrscheinlichste Auslöser der anschließend beobachteten dauerhaften
    // "Fatal network error"-Fehlschläge. Dieselbe Schwelle wie StaleThreshold dient hier als Cooldown
    // zwischen zwei erzwungenen Reconnects, unabhängig davon, ob inzwischen wieder eine Nachricht kam.
    private DateTime? _lastForcedReconnectUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckOnceAsync(stoppingToken);
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;

            if (!twitchChatManager.IsConnected)
            {
                if (twitchChatManager.ConnectAttemptedUtc is null)
                {
                    return; // Initialize()/ConnectAsync() noch nicht erreicht — nichts zu prüfen.
                }

                if (IsInCooldown(now, DisconnectedCooldown))
                {
                    return;
                }

                logger.LogWarning("TwitchClient meldet sich als getrennt, erzwinge Reconnect.");
                _lastForcedReconnectUtc = now;
                await twitchChatManager.ForceReconnectAsync();
                return;
            }

            // Falls back to the connect attempt: LastMessageReceivedUtc stays null until the very
            // first chat message ever received, and the watchdog used to return on every single
            // tick while it was null. A worker that came up but joined nothing (all boot joins
            // failed) was therefore permanently undetectable — no reconnect, no crash, no signal.
            var reference = twitchChatManager.LastMessageReceivedUtc ?? twitchChatManager.ConnectAttemptedUtc;
            if (reference is null)
            {
                return;
            }

            var idleFor = now - reference.Value;
            if (idleFor < StaleThreshold || IsInCooldown(now, StaleThreshold))
            {
                return;
            }

            logger.LogWarning(
                "Keine Chat-Nachricht seit {IdleSeconds}s empfangen (Schwelle {ThresholdSeconds}s), erzwinge Reconnect.",
                (int)idleFor.TotalSeconds, (int)StaleThreshold.TotalSeconds);
            _lastForcedReconnectUtc = now;
            await twitchChatManager.ForceReconnectAsync();
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Watchdog-Durchlauf darf den Worker-Host nicht mitreißen.
            logger.LogWarning(ex, "Twitch-Connection-Watchdog-Durchlauf fehlgeschlagen.");
        }
    }

    private bool IsInCooldown(DateTime now, TimeSpan cooldown) =>
        _lastForcedReconnectUtc is { } last && now - last < cooldown;
}
