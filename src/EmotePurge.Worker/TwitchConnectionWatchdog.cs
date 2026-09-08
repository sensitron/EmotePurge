using EmotePurge.Core.Twitch;

namespace EmotePurge.Worker;

// Erkennt stille Verbindungsabbrüche, bei denen TwitchLib selbst kein OnDisconnected feuert
// (live beobachtet: ~6 Minuten Stillstand ohne jedes Event, s. Projekt-Notizen 2026-07-24/25).
// Misst seit 2026-08-03 empfangene IRC-Frames (inkl. Twitchs ~5-Minuten-Server-PING) statt
// Chat-Nachrichten — die Entscheidungslogik samt Schwellen liegt pur in TwitchWatchdogPolicy.
public class TwitchConnectionWatchdog(
    ILogger<TwitchConnectionWatchdog> logger,
    ITwitchChatManager twitchChatManager,
    ITwitchAppTokenProvider appTokenProvider,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

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
            var decision = TwitchWatchdogPolicy.Decide(
                twitchChatManager.IsConnected,
                Elapsed(now, twitchChatManager.ConnectAttemptedUtc),
                Elapsed(now, twitchChatManager.LastFrameReceivedUtc),
                Elapsed(now, _lastForcedReconnectUtc));

            if (!decision.ForceReconnect)
            {
                return;
            }

            logger.LogWarning("Erzwinge Reconnect: {Reason}", decision.Reason);
            await LogLiveContextAsync(ct);
            _lastForcedReconnectUtc = now;
            await twitchChatManager.ForceReconnectAsync();
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Watchdog-Durchlauf darf den Worker-Host nicht mitreißen.
            logger.LogWarning(ex, "Twitch-Connection-Watchdog-Durchlauf fehlgeschlagen.");
        }
    }

    // Diagnostic context only, never an input to the decision: knowing that every joined channel is
    // offline *explains* silence, it does not *prove* the connection is alive — the frame timestamp
    // does that. Best effort on the A10 app token; a missing token or a failed Helix call costs
    // nothing but this one log line.
    private async Task LogLiveContextAsync(CancellationToken ct)
    {
        try
        {
            var channels = twitchChatManager.GetRoster().Select(entry => entry.ChannelName).ToList();
            if (channels.Count == 0)
            {
                return;
            }

            var accessToken = await appTokenProvider.GetTokenAsync(ct);
            if (accessToken is null)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var helixClient = scope.ServiceProvider.GetRequiredService<ITwitchHelixClient>();
            var streams = await helixClient.GetLiveStreamsByLoginsAsync(channels, accessToken, ct);
            if (streams is null)
            {
                return;
            }

            logger.LogInformation(
                "Kontext zum erzwungenen Reconnect: {LiveCount} von {TotalCount} gejointen Channels laut Helix live.",
                streams.Count, channels.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Live-Kontext-Abfrage vor Reconnect fehlgeschlagen (ignoriert).");
        }
    }

    private static TimeSpan? Elapsed(DateTime now, DateTime? since) =>
        since is { } utc ? now - utc : null;
}
