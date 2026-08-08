namespace EmotePurge.Api.Auth;

internal static class TwitchClaimTypes
{
    public const string Login = "twitch:login";
    public const string DisplayName = "twitch:display_name";
    public const string AccessToken = "twitch:access_token";
    public const string TokenExpiresAtUtc = "twitch:token_expires_at";

    // Carried in the session cookie rather than a User column: the avatar follows the login, and a
    // DB field would need a migration plus a refresh story for a picture nobody has to have fresh.
    public const string ProfileImageUrl = "twitch:profile_image";

    // Issue time of this session, compared against User.SessionsValidFromUtc on every request so
    // that logout can revoke a session server-side.
    public const string SessionIssuedAtUtc = "twitch:session_issued_at";
}
