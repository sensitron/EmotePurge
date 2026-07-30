using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

public interface ISevenTvSyncService
{
    /// <summary>
    /// Resolves and fully reconciles a channel's active 7TV emote set against Postgres.
    /// Returns the resolved set id plus the channel's 7TV account id (for EventAPI subscriptions),
    /// or null if the channel has no 7TV account/emote set.
    /// </summary>
    Task<SevenTvSyncResult?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default);
}
