namespace EmotePurge.Core.Messaging;

public interface IRedisPublisher
{
    Task PublishAsync(string channel, string message, CancellationToken cancellationToken = default);
}
