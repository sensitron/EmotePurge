using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Worker;

public class Worker(
    ILogger<Worker> logger,
    TwitchChatManager twitchChatManager,
    IRedisSubscriber redisSubscriber,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private const string CommandsChannel = "channel:bot:commands";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        twitchChatManager.Initialize();
        await twitchChatManager.ConnectAsync();

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
            }
        }

        // Echtzeit-Join-Kommandos von der Api
        await redisSubscriber.SubscribeAsync(CommandsChannel, async (_, message) =>
        {
            if (message.StartsWith("JOIN:", StringComparison.Ordinal))
            {
                var channelName = message["JOIN:".Length..];
                logger.LogInformation("Redis-Kommando: joine {Channel}.", channelName);
                await twitchChatManager.JoinChannelAsync(channelName);
            }
        }, stoppingToken);

        // Ab hier passiert alle Arbeit in Event-Handlern; ExecuteAsync bleibt nur am Leben,
        // bis der Host das Shutdown-Token feuert.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
