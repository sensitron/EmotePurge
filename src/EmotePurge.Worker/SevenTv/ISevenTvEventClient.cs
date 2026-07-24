namespace EmotePurge.Worker.SevenTv;

public interface ISevenTvEventClient
{
    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task SubscribeAsync(string channelName, string emoteSetId, CancellationToken cancellationToken = default);

    Task UnsubscribeAsync(string channelName, CancellationToken cancellationToken = default);
}
