using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Worker.SevenTv;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Worker;

// Reconciliation half of the hybrid 7TV sync (since 2026-07-30, docs/DECISIONS.md): the EventAPI
// WebSocket (SevenTvEventWorker) delivers live deltas, this worker periodically re-resolves the
// full truth per channel. The full resync stays mandatory regardless of the WebSocket: 7TV's
// EventAPI has no resume/replay (dispatches missed during its hourly TTL reconnects are gone for
// good), has documented publish gaps, and the REST answer is what catches active-set switches the
// event path missed. History of the 2026-07-25 removal and its 2026-07-30 re-evaluation:
// docs/Untersuchung-7TV-WebSocket-2026-07-30.md.
public class SevenTvPeriodicResyncWorker(
    ILogger<SevenTvPeriodicResyncWorker> logger,
    ITwitchChatManager twitchChatManager,
    BootRecoveryGate bootRecoveryGate,
    ISevenTvEventClient sevenTvEventClient,
    IRedisPublisher redisPublisher,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    // 60s is the pre-WebSocket default; once the event path has proven itself in production the
    // interval is meant to be stretched via configuration (e.g. 300s) — deliberately a manual,
    // observable step instead of an automatic coupling to the feature flag.
    private readonly TimeSpan _resyncInterval =
        TimeSpan.FromSeconds(configuration.GetValue("SevenTv:ResyncIntervalSeconds", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot recovery syncs the same channels; overlapping the two collides on the
        // (ChannelId, SevenTvEmoteId) unique index. See BootRecoveryGate.
        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);

        using var timer = new PeriodicTimer(_resyncInterval);
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

                    var result = await syncService.SyncChannelAsync(channelName, ct);
                    if (result is not null)
                    {
                        // Convergence net for the EventAPI subscriptions, analogous to
                        // EnsureJoinedAsync above: idempotent, and the only path that picks up
                        // set/account switches the event stream missed.
                        sevenTvEventClient.EnsureSubscribed(channelName, result.EmoteSetId, result.SevenTvUserId);

                        // Only on a real change: this loop runs every minute for every channel, so
                        // an unconditional publish would refetch every open page on a timer. What
                        // it does catch is everything the EventAPI missed (no resume/replay).
                        if (result.HasChanges)
                        {
                            await redisPublisher.PublishChannelSyncedAsync(logger, channelName, ct);
                        }
                    }
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
