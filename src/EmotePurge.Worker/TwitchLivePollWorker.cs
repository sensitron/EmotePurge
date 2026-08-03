using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;

namespace EmotePurge.Worker;

// Polls GET /helix/streams for every active channel (idea A10, stage 1) and credits the live
// channels one poll interval's worth of minutes in ChannelLiveDays. Runs on the app access token
// (client credentials) — the endpoint needs no scope, and the worker holds no user tokens.
// Deliberately independent of BootRecoveryGate: it only writes coverage rows, which nothing in
// boot recovery touches, and an immediate first poll makes a worker restart observable right away.
public class TwitchLivePollWorker(
    ILogger<TwitchLivePollWorker> logger,
    ITwitchAppTokenProvider appTokenProvider,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    // 300s default: minute-precise coverage is not the goal (the consumer is a per-day marker),
    // and 12 requests/hour stay far below the app token's Helix rate budget.
    private readonly TimeSpan _pollInterval =
        TimeSpan.FromSeconds(configuration.GetValue("Twitch:LivePollIntervalSeconds", 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without client credentials every tick would fail on the token fetch — say it once
        // instead of warning forever. The Api shares the same config values, so a correctly
        // configured deployment cannot land here.
        if (string.IsNullOrEmpty(configuration["Auth:Twitch:ClientId"]) ||
            string.IsNullOrEmpty(configuration["Auth:Twitch:ClientSecret"]))
        {
            logger.LogWarning("Twitch-Live-Poll deaktiviert — Auth:Twitch:ClientId/ClientSecret nicht konfiguriert.");
            return;
        }

        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            await PollOnceAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var channelService = scope.ServiceProvider.GetRequiredService<IChannelService>();
            var activeChannels = await channelService.ListActiveChannelNamesAsync(ct);
            if (activeChannels.Count == 0)
            {
                return;
            }

            var accessToken = await appTokenProvider.GetTokenAsync(ct);
            if (accessToken is null)
            {
                logger.LogWarning("Live-Poll übersprungen — kein Twitch-App-Token verfügbar.");
                return;
            }

            var helixClient = scope.ServiceProvider.GetRequiredService<ITwitchHelixClient>();
            var streams = await helixClient.GetLiveStreamsByLoginsAsync(activeChannels, accessToken, ct);
            if (streams is null)
            {
                // Null must not read as "everyone offline". The token is the most likely culprit
                // (revoked by a secret rotation) — drop it so the next tick starts fresh.
                appTokenProvider.Invalidate();
                logger.LogWarning("Live-Poll fehlgeschlagen — Helix-Streams-Abfrage ohne Ergebnis, Tick wird übersprungen.");
                return;
            }

            if (streams.Count == 0)
            {
                return;
            }

            var coverageService = scope.ServiceProvider.GetRequiredService<ILiveCoverageService>();
            var minutes = Math.Max(1, (int)Math.Round(_pollInterval.TotalMinutes));
            var credited = await coverageService.AddLiveMinutesAsync(
                streams.Select(s => s.UserLogin).ToList(),
                DateOnly.FromDateTime(DateTime.UtcNow),
                minutes,
                ct);

            // Only reached when someone is live, so quiet nights stay quiet in the log.
            logger.LogInformation("Live-Poll: {LiveCount} von {TotalCount} Channels live, {Credited} Abdeckungszeilen fortgeschrieben.",
                streams.Count, activeChannels.Count, credited);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regulärer Shutdown.
        }
        catch (Exception ex)
        {
            // Runs forever on a timer — a Postgres hiccup or a Twitch outage must cost one tick,
            // never the worker host (StopHost default would take the usage flush down with it).
            logger.LogWarning(ex, "Live-Poll-Durchlauf fehlgeschlagen.");
        }
    }
}
