using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task<SevenTvTwitchUserIdResult> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        try
        {
            var payload = new { query = GqlUsersQuery, variables = new { q = normalized } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUsersResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // GraphQL errors surface as HTTP 200 with `data: null` (or `users` missing inside it)
            // plus an `errors` array — that's a failed query, not evidence the account is missing.
            // Only a successfully returned, genuinely empty `users` list means "no match".
            if (dto?.Data?.Users is null)
            {
                logger.LogWarning(
                    "7TV-Nutzersuche für {Channel} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?).",
                    normalized);
                return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            var match = dto.Data.Users
                .SelectMany(u => u.Connections)
                .FirstOrDefault(c => c.Platform == "TWITCH" &&
                    string.Equals(c.Username, normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                // Debug for the same reason as the missing emote set below: only SevenTvSyncService
                // calls this, the periodic resync calls it again every 60 seconds for as long as the
                // channel has no 7TV account, and the line that states the finding is the throttled
                // one there. Measured: this one repeated every tick while that one stayed silent.
                logger.LogDebug("Kein 7TV-Twitch-Match für {Channel}.", normalized);
                return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            return SevenTvTwitchUserIdResult.Ok(match.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "7TV-Nutzersuche für {Channel} fehlgeschlagen, wird übersprungen.", normalized);
            return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    public async Task<SevenTvChannelStateResult> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"users/twitch/{twitchUserId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvUserRestDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // A literal JSON `null` body lands here as dto == null; a genuinely empty or malformed
            // body never reaches this line at all — ReadFromJsonAsync throws JsonException first,
            // caught below as Unavailable. Either way this is a broken answer, not a statement about
            // the account, so it must not read as "no emote set" and send the owner off to fix
            // something that is fine.
            if (dto is null)
            {
                logger.LogWarning("7TV-Antwort für Twitch-ID {Id} war leer.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            // The state behind issue #32, and the only one of the four that used to return silently:
            // the account exists, but no emote set is active on the Twitch connection. Debug rather
            // than Information because this runs on every resync tick: the periodic worker asks
            // again every 60 seconds for as long as the channel stays like this, which is ~1440
            // lines a day per affected channel. The line that carries the finding is the one in
            // SevenTvSyncService, which knows the channel name and only speaks when the reason
            // changes; this one adds the Twitch id and is worth the log level it costs only while
            // someone is actually debugging a lookup.
            if (dto.EmoteSet is null)
            {
                logger.LogDebug(
                    "7TV-Account für Twitch-ID {Id} hat kein aktives Emote-Set.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoActiveEmoteSet);
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

            return SevenTvChannelStateResult.Ok(
                new SevenTvChannelState(sevenTvUserId, new SevenTvEmoteSet(dto.EmoteSet.Id, emotes, capacity)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "7TV-Emote-Set-Abruf für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
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
        // JsonException belongs here rather than in the caller's catch: a malformed v4 answer must
        // stay a missing date, not turn the whole channel Unavailable. The caller now treats
        // JsonException as a broken lookup, which is right for the v3 payload it reads itself — but
        // this optional overlay has always been allowed to fail on its own without taking the sync
        // with it, and letting its parse errors bubble would quietly reverse that.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex,
                "7TV-v4-addedAt-Abruf für Set {SetId} fehlgeschlagen — Beitrittsdaten bleiben vorerst unbekannt.",
                emoteSetId);
            return null;
        }
    }
}
