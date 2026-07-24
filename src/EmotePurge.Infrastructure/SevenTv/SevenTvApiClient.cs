using System.Net;
using System.Net.Http.Json;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.SevenTv;

public class SevenTvApiClient(HttpClient httpClient, ILogger<SevenTvApiClient> logger) : ISevenTvApiClient
{
    private const string GqlUsersQuery =
        "query($q: String!) { users(query: $q) { id username connections { platform username id } } }";

    public async Task<string?> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();

        try
        {
            var payload = new { query = GqlUsersQuery, variables = new { q = normalized } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUsersResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            var match = dto?.Data?.Users
                .SelectMany(u => u.Connections)
                .FirstOrDefault(c => c.Platform == "TWITCH" &&
                    string.Equals(c.Username, normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                logger.LogInformation("Kein 7TV-Twitch-Match für {Channel}.", normalized);
            }

            return match?.Id;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Nutzersuche für {Channel} fehlgeschlagen, wird übersprungen.", normalized);
            return null;
        }
    }

    public async Task<SevenTvEmoteSet?> GetEmoteSetForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"users/twitch/{twitchUserId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvUserRestDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            if (dto?.EmoteSet is null)
            {
                return null;
            }

            var emotes = dto.EmoteSet.Emotes.Select(SevenTvEmoteJsonMapper.MapDto).ToList();
            return new SevenTvEmoteSet(dto.EmoteSet.Id, emotes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Emote-Set-Abruf für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return null;
        }
    }
}
