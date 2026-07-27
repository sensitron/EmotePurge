using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public enum VoteCastResult
{
    Success,
    ChannelNotFound,
    SessionNotFound,
    SessionEnded,
    EmoteNotEligible
}

public interface IVoteSessionService
{
    // null = channel not found/joined. Throws ArgumentException if title is empty, allowedVoterRoles
    // is 0, includes VIPs, or startedAt is in the future (defense-in-depth — the endpoint validates
    // the same upfront). startedAt null = defaults to now.
    Task<VoteSession?> CreateAsync(string channelName, string title, AllowedRoles allowedVoterRoles, DateTime? startedAt = null, CancellationToken cancellationToken = default);

    // null = channel/session not found or session doesn't belong to that channel. Idempotent no-op if already ended.
    Task<VoteSession?> EndAsync(string channelName, long sessionId, CancellationToken cancellationToken = default);

    Task<(VoteCastResult Result, Vote? Vote)> CastVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, VoteType type, CancellationToken cancellationToken = default);
}
