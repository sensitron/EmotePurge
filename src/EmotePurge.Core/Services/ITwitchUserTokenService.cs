namespace EmotePurge.Core.Services;

// AccessToken is null when no usable token could be produced right now. ReauthRequired
// distinguishes the two reasons: true means only a fresh login can help (no refresh token
// stored, Twitch declared it invalid, or the stored scopes no longer cover what we request);
// false means the failure was transient and the next demand may succeed on its own.
public record TwitchUserTokenResult(string? AccessToken, bool ReauthRequired);

// The one path through which Helix consumers obtain a user access token. Serves the cookie-claim
// token while it is still valid (no DB touch), then falls back to the server-side token store,
// refreshing on demand — single-flight per user — when the stored access token has expired.
public interface ITwitchUserTokenService
{
    Task<TwitchUserTokenResult> GetValidAccessTokenAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);
}
