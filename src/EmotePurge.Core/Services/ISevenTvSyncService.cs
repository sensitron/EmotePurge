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

    /// <summary>
    /// Applies one 7TV EventAPI dispatch delta to a single channel, under the same per-channel
    /// gate the full resync uses, followed by a full match-cache reload. Applies nothing when
    /// <paramref name="emoteSetId"/> is no longer the channel's active set (a still-live
    /// subscription on a set the channel has switched away from) or when the delta would wipe a
    /// non-empty channel (implausible; see the empty-set guard in SyncChannelAsync).
    /// Callers — not this method — run the follow-up full resync those outcomes ask for:
    /// SyncChannelAsync takes the same non-reentrant gate and would deadlock if called from here.
    /// </summary>
    Task<SevenTvDeltaOutcome> ApplyEmoteSetUpdateAsync(
        string channelName,
        string emoteSetId,
        SevenTvEmoteSetDelta delta,
        CancellationToken cancellationToken = default);
}
