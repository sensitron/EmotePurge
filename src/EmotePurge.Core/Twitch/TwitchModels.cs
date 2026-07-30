namespace EmotePurge.Core.Twitch;

// RefreshToken/Scopes are null when Twitch omitted them from the token response; Scopes is the
// space-joined form of the response's scope array (same shape as the authorize request parameter).
public record TwitchTokenResult(string AccessToken, DateTime ExpiresAtUtc, string? RefreshToken = null, string? Scopes = null);

// InvalidGrant is Twitch's definitive "this refresh token is dead" answer (HTTP 400,
// "Invalid refresh token") — the stored tokens must be dropped and only a fresh login helps.
// TransientFailure is everything else (5xx, timeout, malformed response): keep the refresh
// token and try again on the next demand.
public enum TwitchRefreshStatus
{
    Success,
    InvalidGrant,
    TransientFailure
}

public record TwitchTokenRefreshResult(TwitchRefreshStatus Status, TwitchTokenResult? Token);

public static class TwitchOAuthDefaults
{
    // Space-delimited scope list sent to id.twitch.tv/oauth2/authorize at login. Also the
    // reference for scope-drift detection: a stored token pair granted with fewer scopes than
    // this cannot be repaired by refreshing — scopes are only ever granted in the authorize flow.
    public const string RequestedScopes = "user:read:email user:read:moderated_channels user:read:subscriptions";
}

public record TwitchUserInfo(string Id, string Login, string DisplayName);

public record TwitchModeratedChannelInfo(string Login, string BroadcasterId);
