using EmotePurge.Core.Messaging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class RedisPublisher(IConnectionMultiplexer connectionMultiplexer) : IRedisPublisher
{
    public async Task PublishAsync(string channel, string message, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();
        await subscriber.PublishAsync(RedisChannel.Literal(channel), message);
    }
}
