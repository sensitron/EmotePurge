using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

public interface ISevenTvSyncService
{
    /// <summary>
    /// Resolves and fully reconciles a channel's active 7TV emote set against Postgres.
    /// Returns the resolved emote-set id (for the caller to WebSocket-subscribe to),
    /// or null if the channel has no 7TV account/emote set.
    /// </summary>
    Task<string?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an incremental delta parsed from a 7TV EventAPI dispatch message.
    /// </summary>
    Task ApplyEmoteSetUpdateAsync(string emoteSetId, SevenTvEmoteSetDelta delta, CancellationToken cancellationToken = default);
}
