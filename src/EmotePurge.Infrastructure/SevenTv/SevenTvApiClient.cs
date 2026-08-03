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

    // v4 schema, not the v3 the BaseAddress points at — the real set-entry added-at only exists
    // there (EmoteSetEmote.addedAt); the v3 payload's timestamp is the emote's upload date. Same
    // snake_case aliasing trick as above, since v4 names everything in camelCase.
    private const string GqlSetEntriesQuery =
        "query($id: Id!, $page: Int!, $perPage: Int!) { emote_sets: emoteSets { emote_set: emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { page_count: pageCount items { added_at: addedAt emote { id } } } } } }";

    // Host-absolute so it escapes the /v3/ BaseAddress.
    private const string V4GqlPath = "/v4/gql";

    // 500 per page keeps even subscriber-sized sets (capacity can exceed 1000) at a handful of
    // requests; the page cap is a runaway guard, not an expected limit.
    private const int SetEntriesPerPage = 500;
    private const int MaxSetEntryPages = 10;

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

            // Overlay the real set-entry dates from v4. Null (lookup failed) simply leaves every
            // AddedToSetAt unknown — the sync's correction pass fills the gap on a later resync,
            // which is strictly better than failing the whole channel sync over a date.
            var addedAtByEmoteId = await GetSetEntryAddedAtAsync(dto.EmoteSet.Id, cancellationToken);
            if (addedAtByEmoteId is not null)
            {
                emotes = emotes
                    .Select(e => addedAtByEmoteId.TryGetValue(e.Id, out var addedAt)
                        ? e with { AddedToSetAt = addedAt }
                        : e)
                    .ToList();
            }

            // The response's top-level id is the Twitch connection id, not the 7TV account —
            // the account lives under user.id (verified live 2026-07-30).
            var sevenTvUserId = string.IsNullOrEmpty(dto.User?.Id) ? null : dto.User.Id;

            // 0 reads as "not reported", not as "no slots" — an absent field and a genuine zero are
            // indistinguishable here, and treating either as a capacity of zero would make the UI
            // claim the set is full.
            var capacity = dto.EmoteSet.Capacity > 0 ? dto.EmoteSet.Capacity : (int?)null;

            return new SevenTvChannelState(sevenTvUserId, new SevenTvEmoteSet(dto.EmoteSet.Id, emotes, capacity));
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

    private async Task<Dictionary<string, DateTime>?> GetSetEntryAddedAtAsync(string emoteSetId, CancellationToken cancellationToken)
    {
        try
        {
            var result = new Dictionary<string, DateTime>();
            for (var page = 1; page <= MaxSetEntryPages; page++)
            {
                var payload = new
                {
                    query = GqlSetEntriesQuery,
                    variables = new { id = emoteSetId, page, perPage = SetEntriesPerPage }
                };
                var response = await httpClient.PostAsJsonAsync(V4GqlPath, payload, cancellationToken);
                response.EnsureSuccessStatusCode();

                var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlSetEntriesResponseDto>(
                    SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

                var entryPage = dto?.Data?.EmoteSets?.EmoteSet?.Emotes;
                if (entryPage is null)
                {
                    logger.LogWarning(
                        "7TV-v4-addedAt-Abruf für Set {SetId} lieferte keine Daten — Beitrittsdaten bleiben vorerst unbekannt.",
                        emoteSetId);
                    return null;
                }

                foreach (var entry in entryPage.Items)
                {
                    if (entry.Emote is not null && entry.AddedAt is not null)
                    {
                        result[entry.Emote.Id] = entry.AddedAt.Value.UtcDateTime;
                    }
                }

                if (page >= entryPage.PageCount)
                {
                    break;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex,
                "7TV-v4-addedAt-Abruf für Set {SetId} fehlgeschlagen — Beitrittsdaten bleiben vorerst unbekannt.",
                emoteSetId);
            return null;
        }
    }
}
