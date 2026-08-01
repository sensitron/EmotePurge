using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;

namespace EmotePurge.Worker;

/// <summary>
/// The worker's single place that turns "this channel's emote inventory really changed" into the
/// thin <see cref="LiveEvents.ChannelSynced"/> event. Shared by all three sync paths (boot recovery
/// and Redis commands in <see cref="Worker"/>, the periodic resync, the EventAPI client) so the
/// swallow-and-log contract exists once: the sync is committed by the time this runs, and a Redis
/// hiccup must never turn a successful sync into a failed one.
/// </summary>
internal static class LiveEventPublisher
{
    public static async Task PublishChannelSyncedAsync(
        this IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.ChannelSynced, ChannelName.Normalize(channelName)).Serialize(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.",
                LiveEvents.ChannelSynced, channelName);
        }
    }
}
