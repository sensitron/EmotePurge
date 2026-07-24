using EmotePurge.Core.Messaging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class RedisSubscriber(IConnectionMultiplexer connectionMultiplexer) : IRedisSubscriber
{
    public async Task SubscribeAsync(string channel, Func<string, string, Task> handler, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(channel), (redisChannel, value) =>
        {
            // Fire-and-forget: StackExchange.Redis handlers are synchronous callbacks,
            // the async handler is intentionally not awaited here.
            _ = handler(redisChannel.ToString(), value.ToString());
        });
    }

    public async Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();
        await subscriber.UnsubscribeAsync(RedisChannel.Literal(channel));
    }
}
