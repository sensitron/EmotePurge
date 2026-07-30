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

/// <summary>
/// Every way creating a vote session can fail. The rules used to exist twice — as
/// <c>400 { errorCode }</c> in the endpoint and as <c>ArgumentException</c> in the service, where they
/// were unreachable. Two copies with divergent failure modes: a fifth rule added only in the service
/// would have produced a 500 instead of a 400, and one added only in the endpoint would have left the
/// service permissive for any other caller (a test, the worker, a later module).
/// The service is now the single authority and the endpoint only translates.
/// </summary>
public enum CreateVoteSessionResult
{
    Success,
    ChannelNotFound,
    TitleEmpty,
    RolesEmpty,
    VipsNotSupported,
    StartedAtInFuture,
    StartedAtTooFarBack
}

public static class VoteSessionLimits
{
    /// <summary>
    /// Backdating <c>StartedAt</c> was unbounded, and the results window is
    /// <c>StartedAt..(EndedAt ?? now)</c> — so one session backdated far enough covered a channel's
    /// entire usage history. Same cap as the usage-stats range, and shared with the endpoint so the
    /// number it reports back cannot drift from the number that is enforced.
    /// </summary>
    public const int MaxBackdateDays = 366;
}

public interface IVoteSessionService
{
    // The service validates; the endpoint maps the result to a status code. startedAt null = now.
    Task<(CreateVoteSessionResult Result, VoteSession? Session)> CreateAsync(
        string channelName, string title, AllowedRoles allowedVoterRoles, DateTime? startedAt = null, CancellationToken cancellationToken = default);

    // null = channel/session not found or session doesn't belong to that channel. Idempotent no-op if already ended.
    Task<VoteSession?> EndAsync(string channelName, long sessionId, CancellationToken cancellationToken = default);

    Task<(VoteCastResult Result, Vote? Vote)> CastVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, VoteType type, CancellationToken cancellationToken = default);

    // Removes the caller's vote for this emote, returning to the neutral (unvoted) state. Idempotent
    // no-op (still Success) if no vote existed — mirrors EndAsync's idempotency.
    Task<VoteCastResult> RetractVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, CancellationToken cancellationToken = default);

    // Hard delete — cascades to the session's Votes (DeleteBehavior.Cascade in AppDbContext).
    // No IsActive guard: a manager may delete an active session too, same as LeaveAsync being
    // usable regardless of a channel's vote-session state. Returns false if the channel or session
    // isn't found (bool convention, mirrors ChannelService.LeaveAsync).
    Task<bool> DeleteAsync(string channelName, long sessionId, CancellationToken cancellationToken = default);
}
