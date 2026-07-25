namespace EmotePurge.Core.Services;

// AccessToken is null when the caller's Twitch access token has already expired (no refresh
// flow in this pass — see CLAUDE.md decision log) — the moderator check is then skipped and only
// the admin-allowlist/broadcaster checks (which don't need a live Twitch token) can still succeed.
public record TwitchPrincipalInfo(string TwitchUserId, string TwitchLogin, string? AccessToken);

public interface IChannelAccessService
{
    Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);
}
