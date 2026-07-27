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
            var lastMessage = twitchChatManager.LastMessageReceivedUtc;
            if (lastMessage is null)
            {
                return; // Noch keine einzige Nachricht seit Start empfangen — nichts zu prüfen.
            }

            var idleFor = DateTime.UtcNow - lastMessage.Value;
            if (idleFor < StaleThreshold)
            {
                return;
            }

            var sinceLastForcedReconnect = _lastForcedReconnectUtc is null
                ? (TimeSpan?)null
                : DateTime.UtcNow - _lastForcedReconnectUtc.Value;
            if (sinceLastForcedReconnect is not null && sinceLastForcedReconnect < StaleThreshold)
            {
                return; // Cooldown aktiv — erst kürzlich reconnectet, ohne dass seitdem eine Nachricht kam.
            }

            logger.LogWarning(
                "Keine Chat-Nachricht seit {IdleSeconds}s empfangen (Schwelle {ThresholdSeconds}s), erzwinge Reconnect.",
                (int)idleFor.TotalSeconds, (int)StaleThreshold.TotalSeconds);
            _lastForcedReconnectUtc = DateTime.UtcNow;
            await twitchChatManager.ForceReconnectAsync();
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Watchdog-Durchlauf darf den Worker-Host nicht mitreißen.
            logger.LogWarning(ex, "Twitch-Connection-Watchdog-Durchlauf fehlgeschlagen.");
        }
    }
}
