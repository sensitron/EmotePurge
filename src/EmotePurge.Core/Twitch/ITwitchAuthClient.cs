namespace EmotePurge.Core.Twitch;

public interface ITwitchAuthClient
{
    Task<TwitchTokenResult?> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default);

    Task<TwitchTokenRefreshResult> RefreshUserTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    // GET id.twitch.tv/oauth2/validate — true: token valid, false: Twitch says it is not (401),
    // null: could not tell (transient failure); callers should then keep using the token.
    Task<bool?> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default);

    // POST id.twitch.tv/oauth2/token, grant_type=client_credentials: an app access token bound to
    // the app, not a user — no scopes, no refresh token (a new grant replaces it), ~60-day expiry.
    // Deliberately separate from the user flows above: it must never touch the user scopes in
    // TwitchOAuthDefaults.RequestedScopes. Null = failure (already logged).
    Task<TwitchTokenResult?> GetAppAccessTokenAsync(CancellationToken cancellationToken = default);
}
