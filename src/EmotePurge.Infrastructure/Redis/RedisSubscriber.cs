using EmotePurge.Core.Messaging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class RedisSubscriber(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisSubscriber> logger) : IRedisSubscriber
{
    public async Task SubscribeAsync(string channel, Func<string, string, Task> handler, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(channel), (redisChannel, value) =>
        {
            // Fire-and-forget: StackExchange.Redis handlers are synchronous callbacks,
            // the async handler is intentionally not awaited here. Exceptions are caught
            // and logged in HandleSafelyAsync instead of vanishing as an unobserved task
            // exception — an unhandled throw here used to fail completely silently.
            _ = HandleSafelyAsync(handler, redisChannel.ToString(), value.ToString());
        });
    }

    private async Task HandleSafelyAsync(Func<string, string, Task> handler, string channel, string message)
    {
        try
        {
            await handler(channel, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unbehandelte Exception im Redis-Subscribe-Handler für Channel {Channel}, Nachricht {Message}.", channel, message);
        }
    }

    public async Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();
        await subscriber.UnsubscribeAsync(RedisChannel.Literal(channel));
    }
}
