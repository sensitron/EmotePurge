using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Worker;

// Alleiniger 7TV-Sync-Mechanismus (2026-07-25): Live-Updates über die 7TV-EventAPI-WebSocket
// wurden entfernt, nachdem sich über mehrere Live-Tests hinweg zeigte, dass Dispatches (auch
// nach Umstellung auf Wildcard-Subscription-Typ + channel-scoped Subscription, passend zum
// offiziellen 7TV-Browser-Extension-Referenzclient) nicht zuverlässig ankommen — teils
// mehrminütige Verzögerung, teils gar nicht, ohne erkennbaren Grund auf unserer Seite. Der
// REST-Vollsync war in jedem Test zuverlässig; ein 1-Minuten-Takt macht die Verzögerung in der
// Praxis irrelevant, bei vernachlässigbaren Kosten (ein 7TV-Request pro aktivem Channel und
// Minute). S. CLAUDE.md-Entscheidungslog für Details.
public class SevenTvPeriodicResyncWorker(
    ILogger<SevenTvPeriodicResyncWorker> logger,
    ITwitchChatManager twitchChatManager,
    BootRecoveryGate bootRecoveryGate,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot recovery syncs the same channels; overlapping the two collides on the
        // (ChannelId, SevenTvEmoteId) unique index. See BootRecoveryGate.
        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);

        using var timer = new PeriodicTimer(ResyncInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ResyncOnceAsync(stoppingToken);
        }
    }

    private async Task ResyncOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();

            // Runs every minute forever, so anything unguarded here is a permanent liability: a
            // Postgres restart, a failover, an exhausted connection pool or a hiccup in the Docker
            // bridge network would escape ExecuteAsync and stop the whole host (StopHost default),
            // taking the buffered usage counts of the current flush window with it.
            var activeChannels = await db.Channels
                .Where(c => c.IsBotActive)
                .Select(c => c.ChannelName)
                .ToListAsync(ct);

            foreach (var channelName in activeChannels)
            {
                try
                {
                    // Convergence net for channels the bot should be in but isn't: a lost Redis
                    // command, a join that failed during boot recovery, or one Twitch never
                    // confirmed. Skips channels already joined and confirmed, so healthy channels
                    // don't get a JOIN every minute.
                    await twitchChatManager.EnsureJoinedAsync(channelName);

                    await syncService.SyncChannelAsync(channelName, ct);
                }
                catch (Exception ex)
                {
                    // Ein fehlgeschlagener Resync für einen Channel darf weder die anderen Channels
                    // in diesem Tick noch den Worker-Host beeinträchtigen.
                    logger.LogWarning(ex, "Periodischer 7TV-Resync für {Channel} fehlgeschlagen.", channelName);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regulärer Shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Periodischer 7TV-Resync-Durchlauf fehlgeschlagen.");
        }
    }
}
