namespace EmotePurge.Core.Twitch;

public interface ITwitchAuthClient
{
    Task<TwitchTokenResult?> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default);
}
