using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public record VoteSessionSummaryDto(long Id, string Title, AllowedRoles AllowedVoterRoles, bool IsActive, DateTime StartedAt, DateTime? EndedAt);

public record VoteSessionResultDto(
    string EmoteId, string EmoteName, string ImageUrl, int TotalUseCount, double NormalizedUsageScore,
    int KeepVotes, int DeleteVotes, double Score);

public record VoteSessionResultsDto(long SessionId, string Title, bool IsActive, DateTime StartedAt, DateTime? EndedAt, IReadOnlyList<VoteSessionResultDto> Emotes);

public interface IVoteSessionQueryService
{
    Task<IReadOnlyList<VoteSessionSummaryDto>> ListSessionsAsync(string channelName, CancellationToken cancellationToken = default);

    // null = channel or session not found / session doesn't belong to that channel.
    Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, CancellationToken cancellationToken = default);
}
