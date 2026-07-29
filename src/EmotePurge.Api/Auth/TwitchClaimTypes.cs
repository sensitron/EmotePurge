namespace EmotePurge.Api.Auth;

internal static class TwitchClaimTypes
{
    public const string Login = "twitch:login";
    public const string DisplayName = "twitch:display_name";
    public const string AccessToken = "twitch:access_token";
    public const string TokenExpiresAtUtc = "twitch:token_expires_at";

    // Issue time of this session, compared against User.SessionsValidFromUtc on every request so
    // that logout can revoke a session server-side.
    public const string SessionIssuedAtUtc = "twitch:session_issued_at";
}
