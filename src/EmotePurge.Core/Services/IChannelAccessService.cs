namespace EmotePurge.Core.Services;

// AccessToken is null when the caller's Twitch access token has already expired (no refresh
// flow in this pass — see CLAUDE.md decision log) — the moderator check is then skipped and only
// the admin-allowlist/broadcaster checks (which don't need a live Twitch token) can still succeed.
public record TwitchPrincipalInfo(string TwitchUserId, string TwitchLogin, string? AccessToken);

public interface IChannelAccessService
{
    Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);

    // Weaker than CanManageChannelAsync: additionally lets a channel's 7TV editors (per 7TV's own
    // editor_of relationship, not a Twitch role) view its usage stats — but NOT join/leave the bot,
    // manage vote sessions, or anything else CanManageChannelAsync gates.
    Task<bool> CanViewUsageStatsAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);

    // Channel-independent check for the admin allowlist (Auth:AdminTwitchLogins) — used by
    // endpoints that aren't scoped to a single channel, e.g. the admin "list all channels" overview.
    bool IsGlobalAdmin(TwitchPrincipalInfo principal);
}
