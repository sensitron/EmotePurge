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
    IEmoteMatchCache emoteMatchCache,
    BootRecoveryGate bootRecoveryGate,
    ISevenTvEventClient sevenTvEventClient,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private const string CommandsChannel = "channel:bot:commands";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        twitchChatManager.Initialize();

        // Does not necessarily return connected: the reconnection policy retries indefinitely in
        // the background, and ConnectAsync only bounds how long we wait for it. Boot recovery runs
        // either way — joins record their intent and get retried once the connection is up.
        await twitchChatManager.ConnectAsync();

        await RunBootRecoveryAsync(stoppingToken);

        // Echtzeit-Join-/Leave-Kommandos von der Api
        await redisSubscriber.SubscribeAsync(CommandsChannel, async (_, message) =>
        {
            if (message.StartsWith("JOIN:", StringComparison.Ordinal))
            {
                var channelName = message["JOIN:".Length..];
                logger.LogInformation("Redis-Kommando: joine {Channel}.", channelName);
                await twitchChatManager.JoinChannelAsync(channelName);
                await SyncSevenTvAsync(channelName, stoppingToken);
            }
            else if (message.StartsWith("LEAVE:", StringComparison.Ordinal))
            {
                var channelName = message["LEAVE:".Length..];
                logger.LogInformation("Redis-Kommando: verlasse {Channel}.", channelName);
                emoteMatchCache.RemoveChannel(channelName);
                sevenTvEventClient.Unsubscribe(channelName);
                await twitchChatManager.LeaveChannelAsync(channelName);
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

    private async Task SyncSevenTvAsync(string channelName, CancellationToken ct)
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
        }
    }
}
