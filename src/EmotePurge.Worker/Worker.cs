using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.SevenTv;

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

        // Only now may anything publish a command the worker has to act on. SubscribeAsync has
        // returned, which means Redis acknowledged the SUBSCRIBE and the ChannelMessageQueue is
        // buffering — a message published from here on is delivered even if OnMessage's first
        // callback has not run yet. Before this point Redis would have thrown the message away
        // without an error (issue #54).
        bootRecoveryGate.MarkCommandChannelSubscribed();

        // Ab hier passiert alle Arbeit in Event-Handlern; ExecuteAsync bleibt nur am Leben,
        // bis der Host das Shutdown-Token feuert.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    // Boot-Recovery (docs/Architectur.md Grundsatz 3)
    private async Task RunBootRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var channelService = scope.ServiceProvider.GetRequiredService<IChannelService>();
            var activeChannels = await channelService.ListActiveChannelNamesAsync(stoppingToken);

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
    /// Announces the finished sync as a live event even when it changed nothing. Only set for the
    /// two user-triggered paths (a JOIN and an admin RESYNC), where somebody is waiting in front of
    /// a screen and the event doubles as "done" feedback. Unattended paths (boot recovery here, the
    /// periodic resync, the EventAPI follow-ups) leave it off and publish only on a real change:
    /// a per-minute no-op resync must not make every open page refetch on a timer.
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

            if (publishCompletion || result.HasChanges)
            {
                await redisPublisher.PublishChannelSyncedAsync(logger, channelName, ct);
            }
        }
    }
}
