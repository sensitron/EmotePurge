namespace EmotePurge.Core.Services;

/// <summary>
/// One row of the global-admin channel list. Counts follow a total/subset pairing:
/// <paramref name="EmoteCount"/> and <paramref name="VoteSessionCount"/> are the full row counts,
/// <paramref name="ArchivedEmoteCount"/> and <paramref name="ActiveVoteSessionCount"/> the subsets —
/// so the UI can render "12 (3 archived)" without a second request or client-side arithmetic that
/// could disagree with the database.
/// </summary>
/// <param name="LastSyncedAtUtc">
/// When a full 7TV REST sync last completed for this channel, successful or not-a-single-change
/// alike. Null when none has completed since the column exists. This is the number that answers
/// "is the sync running at all".
/// </param>
/// <param name="LiveState">
/// One of <see cref="ChannelLiveStates"/>. Not a database fact: the endpoint layers it on from the
/// worker's live-status snapshot, the query service always emits the default.
/// </param>
/// <param name="LastInventoryChangeUtc">
/// Newest <c>Emote.LastSyncedAt</c> of the channel: when the emote inventory last actually moved.
/// Null for a channel with no emote rows at all. Deliberately not the same as
/// <paramref name="LastSyncedAtUtc"/> — a healthy channel whose set nobody edits shows a fresh sync
/// and an ancient inventory change, and reporting only the latter made that look like a stalled bot.
/// </param>
public record AdminChannelDto(
    string ChannelName,
    string? TwitchChannelId,
    bool IsBotActive,
    DateTime CreatedAt,
    int EmoteCount,
    int ArchivedEmoteCount,
    int ActiveVoteSessionCount,
    int VoteSessionCount,
    DateTime? LastSyncedAtUtc,
    DateTime? LastInventoryChangeUtc,
    string? ActiveEmoteSetId = null,
    int? ActiveEmoteSetCapacity = null,
    DateTime? TrackingResumedAt = null,
    string LiveState = ChannelLiveStates.Unknown);

/// <summary>
/// Read model behind GET /api/admin/channels: every tracked channel with the aggregates an admin
/// needs to judge it at a glance. Deliberately separate from <see cref="IChannelService"/>, which
/// owns the write side (join/leave/purge).
/// </summary>
public interface IAdminChannelQueryService
{
    Task<IReadOnlyList<AdminChannelDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One channel by name, for the support drilldown. <c>null</c> when it is not tracked at all —
    /// the same row shape as the list, so the two views cannot report different numbers.
    /// </summary>
    Task<AdminChannelDto?> GetAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Just the names of the channels the bot should currently be in — the "should" side of the
    /// roster comparison. Separate from <see cref="ListAsync"/> because the roster is refetched on
    /// every worker.roster event and has no use for three aggregate queries over emotes and votes.
    /// </summary>
    Task<IReadOnlyList<string>> ListActiveChannelNamesAsync(CancellationToken cancellationToken = default);
}
