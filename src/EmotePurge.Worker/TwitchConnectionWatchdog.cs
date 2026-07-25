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

            logger.LogWarning(
                "Keine Chat-Nachricht seit {IdleSeconds}s empfangen (Schwelle {ThresholdSeconds}s), erzwinge Reconnect.",
                (int)idleFor.TotalSeconds, (int)StaleThreshold.TotalSeconds);
            await twitchChatManager.ForceReconnectAsync();
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Watchdog-Durchlauf darf den Worker-Host nicht mitreißen.
            logger.LogWarning(ex, "Twitch-Connection-Watchdog-Durchlauf fehlgeschlagen.");
        }
    }
}
