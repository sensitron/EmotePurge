using EmotePurge.Core.Messaging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class RedisSubscriber(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisSubscriber> logger) : IRedisSubscriber
{
    public async Task SubscribeAsync(string channel, Func<string, string, Task> handler, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();

        // ChannelMessageQueue instead of the synchronous callback: StackExchange.Redis delivers
        // messages of one channel in order, and OnMessage with an async delegate awaits each
        // handler before starting the next. The previous fire-and-forget gave that ordering up —
        // a JOIN and a LEAVE for the same channel could then complete in reverse order and leave
        // the bot permanently sitting in a channel it had been told to leave.
        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(channel));
        queue.OnMessage(async message =>
        {
            var value = message.Message.ToString();
            try
            {
                await handler(message.Channel.ToString(), value);
            }
            catch (Exception ex)
            {
                // Without this the exception would vanish unobserved: a duplicate emote name once
                // silenced a channel's entire sync that way, with no log line anywhere.
                logger.LogError(ex, "Unbehandelte Exception im Redis-Subscribe-Handler für Channel {Channel}, Nachricht {Message}.", channel, value);
            }
        });
    }

    public async Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        var subscriber = connectionMultiplexer.GetSubscriber();
        await subscriber.UnsubscribeAsync(RedisChannel.Literal(channel));
    }
}
