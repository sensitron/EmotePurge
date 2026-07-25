namespace EmotePurge.Core.Services;

public interface ISevenTvSyncService
{
    /// <summary>
    /// Resolves and fully reconciles a channel's active 7TV emote set against Postgres.
    /// Returns the resolved emote-set id, or null if the channel has no 7TV account/emote set.
    /// </summary>
    Task<string?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default);
}
