using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Worker.SevenTv;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Worker;

public class Worker(
    ILogger<Worker> logger,
    ITwitchChatManager twitchChatManager,
    IRedisSubscriber redisSubscriber,
    IRedisPublisher redisPublisher,
    IEmoteMatchCache emoteMatchCache,
    BootRecoveryGate bootRecoveryGate,
    ISevenTvEventClient sevenTvEventClient,
    IServiceScopeFactory scopeFactory) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        twitchChatManager.Initialize();

        // Does not necessarily return connected: the reconnection policy retries indefinitely in
        // the background, and ConnectAsync only bounds how long we wait for it. Boot recovery runs
        // either way — joins record their intent and get retried once the connection is up.
        await twitchChatManager.ConnectAsync();

        await RunBootRecoveryAsync(stoppingToken);

        // Echtzeit-Join-/Leave-/Resync-Kommandos von der Api
        await redisSubscriber.SubscribeAsync(BotCommands.Channel, async (_, message) =>
        {
            if (message.StartsWith(BotCommands.JoinPrefix, StringComparison.Ordinal))
            {
                var channelName = message[BotCommands.JoinPrefix.Length..];
                logger.LogInformation("Redis-Kommando: joine {Channel}.", channelName);
                await twitchChatManager.JoinChannelAsync(channelName);
                await SyncSevenTvAsync(channelName, stoppingToken, publishCompletion: true);
            }
            else if (message.StartsWith(BotCommands.LeavePrefix, StringComparison.Ordinal))
            {
                var channelName = message[BotCommands.LeavePrefix.Length..];
                logger.LogInformation("Redis-Kommando: verlasse {Channel}.", channelName);
                emoteMatchCache.RemoveChannel(channelName);
                sevenTvEventClient.Unsubscribe(channelName);
                await twitchChatManager.LeaveChannelAsync(channelName);
            }
            else if (message.StartsWith(BotCommands.ResyncPrefix, StringComparison.Ordinal))
            {
                // Admin-getriggerter Sofort-Resync: gleiche Schritte wie ein Tick des periodischen
                // Resyncs für genau diesen Channel (EnsureJoined als Konvergenznetz inklusive).
                var channelName = message[BotCommands.ResyncPrefix.Length..];
                logger.LogInformation("Redis-Kommando: resynce {Channel}.", channelName);
                await twitchChatManager.EnsureJoinedAsync(channelName);
                await SyncSevenTvAsync(channelName, stoppingToken, publishCompletion: true);
            }
        }, stoppingToken);

        // Ab hier passiert alle Arbeit in Event-Handlern; ExecuteAsync bleibt nur am Leben,
        // bis der Host das Shutdown-Token feuert.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    // Boot-Recovery (Architectur.md Grundsatz 3)
    private async Task RunBootRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var activeChannels = await db.Channels
                .Where(c => c.IsBotActive)
                .Select(c => c.ChannelName)
                .ToListAsync(stoppingToken);

            foreach (var channelName in activeChannels)
            {
                try
                {
                    logger.LogInformation("Boot-Recovery: joine {Channel}.", channelName);
                    await twitchChatManager.JoinChannelAsync(channelName);
                    await SyncSevenTvAsync(channelName, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Unlike JoinChannelAsync, SyncChannelAsync can throw (JsonException from 7TV,
                    // DbUpdateException on the (ChannelId, SevenTvEmoteId) unique index). Escaping
                    // ExecuteAsync would stop the whole host (BackgroundServiceExceptionBehavior
                    // defaults to StopHost), and since boot recovery runs in the same order every
                    // time, the restart would hit the same channel again — a crash loop in which
                    // every channel behind the failing one is never joined at all.
                    logger.LogWarning(ex, "Boot-Recovery für {Channel} fehlgeschlagen.", channelName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Boot-Recovery fehlgeschlagen — Channels werden erst über den periodischen Resync nachgezogen.");
        }
        finally
        {
            // Releases the periodic resync worker even if boot recovery failed, so a broken boot
            // never turns into a permanently blocked convergence path.
            bootRecoveryGate.MarkCompleted();
        }
    }

    /// <param name="publishCompletion">
    /// Announces the finished sync as a live event. Only set for the two user-triggered paths (a
    /// JOIN and an admin RESYNC), where somebody is waiting in front of a screen — boot recovery and
    /// the periodic resync run unattended and would just make every open admin page reload on a
    /// timer.
    /// </param>
    private async Task SyncSevenTvAsync(string channelName, CancellationToken ct, bool publishCompletion = false)
    {
        using var scope = scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
        var result = await syncService.SyncChannelAsync(channelName, ct);
        if (result is not null)
        {
            logger.LogInformation("7TV-Set {SetId} für {Channel} synchronisiert.", result.EmoteSetId, channelName);

            // Desired-state first: safe even before the EventAPI session exists; the client
            // converges the socket towards the registry after every Hello.
            sevenTvEventClient.EnsureSubscribed(channelName, result.EmoteSetId, result.SevenTvUserId);

            if (publishCompletion)
            {
                try
                {
                    await redisPublisher.PublishAsync(
                        LiveEvents.Channel,
                        new LiveEvent(LiveEvents.ChannelSynced, ChannelName.Normalize(channelName)).Serialize(),
                        ct);
                }
                catch (Exception ex)
                {
                    // The sync itself is done and persisted; a failed notification must not turn it
                    // into a failed command (boot recovery would otherwise log it as a failure).
                    logger.LogWarning(ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.", LiveEvents.ChannelSynced, channelName);
                }
            }
        }
    }
}
