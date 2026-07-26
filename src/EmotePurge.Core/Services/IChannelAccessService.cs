namespace EmotePurge.Core.Services;

// AccessToken is null when the caller's Twitch access token has already expired (no refresh
// flow in this pass — see CLAUDE.md decision log) — the moderator check is then skipped and only
// the admin-allowlist/broadcaster checks (which don't need a live Twitch token) can still succeed.
public record TwitchPrincipalInfo(string TwitchUserId, string TwitchLogin, string? AccessToken);

public interface IChannelAccessService
{
    Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);

    // Channel-independent check for the admin allowlist (Auth:AdminTwitchLogins) — used by
    // endpoints that aren't scoped to a single channel, e.g. the admin "list all channels" overview.
    bool IsGlobalAdmin(TwitchPrincipalInfo principal);
}
