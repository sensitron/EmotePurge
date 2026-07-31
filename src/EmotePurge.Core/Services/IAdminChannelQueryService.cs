namespace EmotePurge.Core.Services;

/// <summary>
/// One row of the global-admin channel list. Counts follow a total/subset pairing:
/// <paramref name="EmoteCount"/> and <paramref name="VoteSessionCount"/> are the full row counts,
/// <paramref name="ArchivedEmoteCount"/> and <paramref name="ActiveVoteSessionCount"/> the subsets —
/// so the UI can render "12 (3 archived)" without a second request or client-side arithmetic that
/// could disagree with the database.
/// </summary>
/// <param name="LastSyncedAtUtc">
/// Newest <c>Emote.LastSyncedAt</c> of the channel, i.e. when the 7TV sync last touched it. Null for
/// a channel that has no emote rows at all (freshly joined, or never successfully synced) — which is
/// a different statement than "synced long ago" and must stay distinguishable.
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
    DateTime? LastSyncedAtUtc);

/// <summary>
/// Read model behind GET /api/admin/channels: every tracked channel with the aggregates an admin
/// needs to judge it at a glance. Deliberately separate from <see cref="IChannelService"/>, which
/// owns the write side (join/leave/purge).
/// </summary>
public interface IAdminChannelQueryService
{
    Task<IReadOnlyList<AdminChannelDto>> ListAsync(CancellationToken cancellationToken = default);
}
