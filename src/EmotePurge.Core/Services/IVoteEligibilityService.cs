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
}
