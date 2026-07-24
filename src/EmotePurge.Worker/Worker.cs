using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Worker.SevenTv;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Worker;

public class Worker(
    ILogger<Worker> logger,
    ITwitchChatManager twitchChatManager,
    ISevenTvEventClient sevenTvEventClient,
    IRedisSubscriber redisSubscriber,
    IEmoteMatchCache emoteMatchCache,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private const string CommandsChannel = "channel:bot:commands";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        twitchChatManager.Initialize();
        await twitchChatManager.ConnectAsync();
        await sevenTvEventClient.ConnectAsync(stoppingToken);

        // Boot-Recovery (Architectur.md Grundsatz 3)
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var activeChannels = await db.Channels
                .Where(c => c.IsBotActive)
                .Select(c => c.ChannelName)
                .ToListAsync(stoppingToken);

            foreach (var channelName in activeChannels)
            {
                logger.LogInformation("Boot-Recovery: joine {Channel}.", channelName);
                await twitchChatManager.JoinChannelAsync(channelName);
                await SyncAndSubscribeSevenTvAsync(channelName, stoppingToken);
            }
        }

        // Echtzeit-Join-/Leave-Kommandos von der Api
        await redisSubscriber.SubscribeAsync(CommandsChannel, async (_, message) =>
        {
            if (message.StartsWith("JOIN:", StringComparison.Ordinal))
            {
                var channelName = message["JOIN:".Length..];
                logger.LogInformation("Redis-Kommando: joine {Channel}.", channelName);
                await twitchChatManager.JoinChannelAsync(channelName);
                await SyncAndSubscribeSevenTvAsync(channelName, stoppingToken);
            }
            else if (message.StartsWith("LEAVE:", StringComparison.Ordinal))
            {
                var channelName = message["LEAVE:".Length..];
                logger.LogInformation("Redis-Kommando: verlasse {Channel}.", channelName);
                emoteMatchCache.RemoveChannel(channelName);
                await twitchChatManager.LeaveChannelAsync(channelName);
                await sevenTvEventClient.UnsubscribeAsync(channelName, stoppingToken);
            }
        }, stoppingToken);

        // Ab hier passiert alle Arbeit in Event-Handlern; ExecuteAsync bleibt nur am Leben,
        // bis der Host das Shutdown-Token feuert.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task SyncAndSubscribeSevenTvAsync(string channelName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
        var emoteSetId = await syncService.SyncChannelAsync(channelName, ct);
        if (emoteSetId is not null)
        {
            logger.LogInformation("7TV-Set {SetId} für {Channel} synchronisiert, abonniere.", emoteSetId, channelName);
            await sevenTvEventClient.SubscribeAsync(channelName, emoteSetId, ct);
        }
    }
}
