using System.Net;
using System.Net.Http.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.SevenTv;

public class SevenTvApiClient(HttpClient httpClient, ILogger<SevenTvApiClient> logger) : ISevenTvApiClient
{
    private const string GqlUsersQuery =
        "query($q: String!) { users(query: $q) { id username connections { platform username id } } }";

    // Aliased to snake_case (`user_by_connection`/`emote_set`) so the shared SnakeCaseLower
    // JsonSerializerOptions (see SevenTvEmoteJsonMapper) can match them from PascalCase DTO
    // properties without per-property [JsonPropertyName] attributes — 7TV's schema names these two
    // particular query-root fields in camelCase, unlike every other field this client already reads.
    private const string GqlUserByConnectionQuery =
        "query($p: ConnectionPlatform!, $id: String!) { user_by_connection: userByConnection(platform: $p, id: $id) { id connections { platform id emote_set_id } } }";

    private const string GqlEmoteSetOwnerQuery =
        "query($id: ObjectID!) { emote_set: emoteSet(id: $id) { owner_id } }";

    private const string GqlEditorOfQuery =
        "query($id: ObjectID!) { user(id: $id) { editor_of { user { connections { platform id username } } } } }";

    public async Task<string?> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

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

    public async Task<SevenTvChannelState?> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
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

            // The response's top-level id is the Twitch connection id, not the 7TV account —
            // the account lives under user.id (verified live 2026-07-30).
            var sevenTvUserId = string.IsNullOrEmpty(dto.User?.Id) ? null : dto.User.Id;

            return new SevenTvChannelState(sevenTvUserId, new SevenTvEmoteSet(dto.EmoteSet.Id, emotes));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Emote-Set-Abruf für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return null;
        }
    }

    public async Task<SevenTvIdentity?> ResolveSevenTvIdentityAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlUserByConnectionQuery, variables = new { p = "TWITCH", id = twitchUserId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUserByConnectionResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            var user = dto?.Data?.UserByConnection;
            if (user is null)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return null;
            }

            var activeEmoteSetId = user.Connections
                .FirstOrDefault(c => c.Platform == "TWITCH" && c.Id == twitchUserId)
                ?.EmoteSetId;

            return new SevenTvIdentity(user.Id, activeEmoteSetId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Identitätsauflösung für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return null;
        }
    }

    public async Task<string?> GetEmoteSetOwnerIdAsync(string emoteSetId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlEmoteSetOwnerQuery, variables = new { id = emoteSetId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlEmoteSetOwnerResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            var ownerId = dto?.Data?.EmoteSet?.OwnerId;
            if (ownerId is null)
            {
                logger.LogInformation("Kein Owner für 7TV-Set {SetId} auflösbar.", emoteSetId);
            }

            return ownerId;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Set-Owner-Abruf für Set {SetId} fehlgeschlagen, wird übersprungen.", emoteSetId);
            return null;
        }
    }

    public async Task<IReadOnlyList<SevenTvEditorGrant>?> GetEditorOfChannelsAsync(string sevenTvUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlEditorOfQuery, variables = new { id = sevenTvUserId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlEditorOfResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            var grants = dto?.Data?.User?.EditorOf;
            if (grants is null)
            {
                return null;
            }

            return grants
                .SelectMany(g => g.User?.Connections ?? [])
                .Where(c => c.Platform == "TWITCH")
                .Select(c => new SevenTvEditorGrant(c.Username, c.Id))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Editor-Abfrage für 7TV-User {Id} fehlgeschlagen, wird übersprungen.", sevenTvUserId);
            return null;
        }
    }
}
