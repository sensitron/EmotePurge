using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

// EmoteCount: size of the session's explicit ballot; null = dynamic "all emotes" session.
public record VoteSessionSummaryDto(long Id, string Title, AllowedRoles AllowedVoterRoles, bool IsActive, DateTime StartedAt, DateTime? EndedAt, int? EmoteCount);

// TotalUseCount is manager-only context (null for everyone else — and for archived subset emotes,
// whose usage totals are no longer computed). Score = KeepVotes - DeleteVotes; chat usage is
// deliberately not part of it, managers already spent the usage data when curating the ballot.
// IsArchived marks a subset emote that left the 7TV set mid-session: it stays listed with its
// votes, but casting further votes on it is rejected.
public record VoteSessionResultDto(
    string EmoteId, string EmoteName, string SevenTvEmoteId, string ImageUrl, int? TotalUseCount,
    int KeepVotes, int DeleteVotes, int Score, bool IsArchived, VoteType? MyVote);

// VoterCount = distinct voters across the whole session, so thin results can be labeled as such.
public record VoteSessionResultsDto(long SessionId, string Title, bool IsActive, DateTime StartedAt, DateTime? EndedAt, int VoterCount, IReadOnlyList<VoteSessionResultDto> Emotes);

// A session the given voter has cast at least one vote in, ever — regardless of whether it's since
// ended. LastVotedAt is the max UpdatedAt across that voter's votes in the session, used to order
// "My Votings" by most-recently-interacted-with first.
public record MyVoteSessionDto(long SessionId, string Title, string ChannelName, bool IsActive, DateTime StartedAt, DateTime? EndedAt, DateTime LastVotedAt);

public interface IVoteSessionQueryService
{
    Task<IReadOnlyList<VoteSessionSummaryDto>> ListSessionsAsync(string channelName, CancellationToken cancellationToken = default);

    // DB-level Skip/Take + Count, same ordering (StartedAt descending) as ListSessionsAsync. Only
    // used on the "manager sees everything" path in VoteSessionEndpoints.cs — the non-manager path
    // still needs the full unpaged ListSessionsAsync list to run its per-session eligibility filter
    // first (filter-then-paginate, see VoteSessionEndpoints.cs comment), so that method is left
    // untouched.
    Task<PagedResult<VoteSessionSummaryDto>> ListSessionsPagedAsync(string channelName, int page, int pageSize, CancellationToken cancellationToken = default);

    // null = channel or session not found / session doesn't belong to that channel.
    // viewerTwitchUserId is optional at this service layer (kept nullable for testability), but the
    // Api endpoint always supplies it now — VoteAudienceFilter guarantees an authenticated viewer
    // before this is called, so each result row's MyVote reflects that viewer's own vote.
    // includeRawUsage gates TotalUseCount only: false reports null per emote (data absent, not
    // "used 0 times"), while Score and the ordering stay exactly the same. Raw per-emote chat usage
    // is management data (the usage-stats endpoints are access-filtered for that reason), and a
    // session's audience can include every logged-in user.
    Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, string? viewerTwitchUserId = null, bool includeRawUsage = false, CancellationToken cancellationToken = default);

    // Cross-channel "My Votings": every session the given voter has ever voted in, across all
    // channels, ordered by most-recently-voted-in first. Deliberately not gated by current audience
    // eligibility (VoteEligibilityService) — this is the voter's own history, not a fresh invitation
    // to vote, so an ended or since-restricted session still belongs in their own list.
    Task<PagedResult<MyVoteSessionDto>> ListMyVoteSessionsAsync(string voterTwitchUserId, int page, int pageSize, CancellationToken cancellationToken = default);
}
