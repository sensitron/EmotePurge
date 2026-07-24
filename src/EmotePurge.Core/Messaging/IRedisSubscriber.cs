namespace EmotePurge.Core.Messaging;

public interface IRedisSubscriber
{
    Task SubscribeAsync(string channel, Func<string, string, Task> handler, CancellationToken cancellationToken = default);

    Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default);
}
