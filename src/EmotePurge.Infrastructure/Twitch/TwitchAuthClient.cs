using System.Net.Http.Json;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Twitch;

public class TwitchAuthClient(HttpClient httpClient, IConfiguration configuration, ILogger<TwitchAuthClient> logger) : ITwitchAuthClient
{
    public async Task<TwitchTokenResult?> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        var clientId = configuration["Auth:Twitch:ClientId"]
            ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:ClientId' fehlt.");
        var clientSecret = configuration["Auth:Twitch:ClientSecret"]
            ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:ClientSecret' fehlt.");

        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            };

            var response = await httpClient.PostAsync("oauth2/token", new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch-Token-Exchange fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<TwitchTokenResponseDto>(TwitchJsonOptions.Value, cancellationToken);
            if (dto is null || string.IsNullOrEmpty(dto.AccessToken))
            {
                return null;
            }

            return new TwitchTokenResult(dto.AccessToken, DateTime.UtcNow.AddSeconds(dto.ExpiresIn));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch-Token-Exchange fehlgeschlagen.");
            return null;
        }
    }
}
