namespace EmotePurge.Core.Twitch;

public interface ITwitchAuthClient
{
    Task<TwitchTokenResult?> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default);

    Task<TwitchTokenRefreshResult> RefreshUserTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    // GET id.twitch.tv/oauth2/validate — true: token valid, false: Twitch says it is not (401),
    // null: could not tell (transient failure); callers should then keep using the token.
    Task<bool?> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default);
}
