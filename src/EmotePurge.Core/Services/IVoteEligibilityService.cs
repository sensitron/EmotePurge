namespace EmotePurge.Core.Services;

public enum VoteEligibilityResult
{
    Allowed,
    SessionNotFound,
    SessionEnded,
    RoleNotEligible
}

public interface IVoteEligibilityService
{
    Task<VoteEligibilityResult> EvaluateAsync(
        TwitchPrincipalInfo principal, string channelName, long sessionId, CancellationToken cancellationToken = default);

    // Same role/identity check as EvaluateAsync, but never returns SessionEnded — used to gate VIEW
    // access to a session's results, where "voting closed" should not also retroactively hide the
    // session from someone who was always part of its intended audience.
    Task<VoteEligibilityResult> EvaluateAudienceAsync(
        TwitchPrincipalInfo principal, string channelName, long sessionId, CancellationToken cancellationToken = default);
}
