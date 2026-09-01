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

    // Latches the fallback-set-load path (issue #43) from Information down to Debug after its first
    // occurrence in this process. Once 7TV finishes rolling out the null embedded emote_set, this
    // fallback becomes the permanent path for every channel on every 60s resync tick — an
    // Information line there forever would be exactly the per-tick log spam NoActiveEmoteSet's Debug
    // level (below) already exists to avoid. One Information line still marks the rollout as visibly
    // arrived; every later tick logs Debug like the rest of this method's steady-state paths.
    private static int _fallbackEmoteSetPathLoggedOnce;

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

            var emoteSetDto = dto.EmoteSet;
            if (emoteSetDto is null)
            {
                // Issue #43: 7TV announced (no date) that it will null this embedded object out of
                // the response. Measured live 2026-09-01: this top-level one is still populated,
                // but the same object one level down (user.connections[].emote_set) is already
                // null for every checked channel while the plain-id fields stay filled — the
                // rollout has started, it just has not reached this field yet. Resolve a usable set
                // id from whatever the response still offers and reload the set separately rather
                // than presenting every channel with a false "no active emote set".
                var fallbackEmoteSetId = ResolveFallbackEmoteSetId(dto, twitchUserId);

                // The state behind issue #32, and the only one of the four that used to return
                // silently: the account exists, but no emote set is active on the Twitch connection.
                // Debug rather than Information because this runs on every resync tick: the periodic
                // worker asks again every 60 seconds for as long as the channel stays like this,
                // which is ~1440 lines a day per affected channel. The line that carries the finding
                // is the one in SevenTvSyncService, which knows the channel name and only speaks when
                // the reason changes; this one adds the Twitch id and is worth the log level it costs
                // only while someone is actually debugging a lookup.
                if (fallbackEmoteSetId is null)
                {
                    logger.LogDebug(
                        "7TV-Account für Twitch-ID {Id} hat kein aktives Emote-Set.", twitchUserId);
                    return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoActiveEmoteSet);
                }

                LogFallbackEmoteSetPathEntered(twitchUserId);

                emoteSetDto = await FetchEmoteSetAsync(fallbackEmoteSetId, cancellationToken);
                if (emoteSetDto is null)
                {
                    // A non-success status (EnsureSuccessStatusCode, incl. 404) or a malformed body
                    // already threw and landed in the catch below as Unavailable; only a literal
                    // JSON `null` reload body reaches this branch, and it means the same thing: a
                    // broken reload must not read as "no emote set" (see the null-body guard for the
                    // primary response above).
                    logger.LogWarning(
                        "7TV-Emote-Set-Nachladen für Set {SetId} (Twitch-ID {TwitchId}) war leer.",
                        fallbackEmoteSetId, twitchUserId);
                    return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
                }
            }

            return await BuildChannelStateResultAsync(emoteSetDto, dto.User, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "7TV-Emote-Set-Abruf für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    public async Task<SevenTvIdentityResult> ResolveSevenTvIdentityAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlUserByConnectionQuery, variables = new { p = "TWITCH", id = twitchUserId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUserByConnectionResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // A null user here is a broken GraphQL response (`data: null` plus an `errors`
            // array, or `userByConnection` missing from an otherwise-parseable body), not
            // evidence the account is missing: 7TV never answers a Twitch id with no linked
            // account with a literal null — it returns HTTP 200 with a placeholder user
            // instead, which the connection check right below already catches and maps to
            // NoSevenTvAccount. That placeholder covers the "no account" case completely, so
            // nothing is left for a null user to mean except a failed lookup. Same distinction
            // ResolveTwitchUserIdAsync already draws for its own GraphQL response above; this
            // brings this method in line with it rather than introducing anything new.
            var user = dto?.Data?.UserByConnection;
            if (user is null)
            {
                logger.LogWarning(
                    "7TV-Identitätsauflösung für Twitch-ID {Id} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?).",
                    twitchUserId);
                return SevenTvIdentityResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            // 7TV never returns a literal null for a Twitch id with no linked account: it answers
            // HTTP 200 with a placeholder user instead (measured live 2026-08-31, id
            // "00000000000000000000000000", `connections: []`). 7TV finds the account BY this
            // Twitch connection, so the connection's presence in the answer — not the account id —
            // is the only thing that tells a real match apart from the placeholder; this also
            // survives 7TV changing the sentinel value itself. `EmoteSetId` on that connection may
            // still be null (account exists, no active set), which is a legitimate Ok — only the
            // connection itself being absent means NoSevenTvAccount.
            var twitchConnection = user.Connections
                .FirstOrDefault(c => c.Platform == "TWITCH" && c.Id == twitchUserId);
            if (twitchConnection is null)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return SevenTvIdentityResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            return SevenTvIdentityResult.Ok(new SevenTvIdentity(user.Id, twitchConnection.EmoteSetId));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Identitätsauflösung für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return SevenTvIdentityResult.Failed(SevenTvLookupStatus.Unavailable);
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

    public async Task<SevenTvEditorGrantsResult> GetEditorOfChannelsAsync(string sevenTvUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlEditorOfQuery, variables = new { id = sevenTvUserId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlEditorOfResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // EditorOf on the DTO defaults to an empty list and 7TV's GraphQL schema returns list
            // fields as `[]`, never `null`, for "no entries" — so a genuinely editor-of-nothing
            // account already lands here as an empty (non-null) list, not this branch. Reaching here
            // means the response itself is unusable: a GraphQL error (`data: null`) or `user` coming
            // back null for an id this same call chain already resolved as valid moments earlier.
            // That premise only holds since ResolveSevenTvIdentityAsync stopped treating 7TV's
            // placeholder user (measured live 2026-08-31: HTTP 200, id
            // "00000000000000000000000000", `connections: []`) as a real match — before that fix,
            // this method could receive that placeholder id and its 404 `LOAD_ERROR user not found`
            // landed here too, indistinguishable from a genuinely broken response. Now only an id
            // 7TV already confirmed via a real Twitch connection ever reaches this call, so `user:
            // null` here is a broken lookup (Unavailable), never "no account".
            var grants = dto?.Data?.User?.EditorOf;
            if (grants is null)
            {
                logger.LogWarning(
                    "7TV-Editor-Abfrage für 7TV-User {Id} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?).",
                    sevenTvUserId);
                return SevenTvEditorGrantsResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            var result = grants
                .SelectMany(g => g.User?.Connections ?? [])
                .Where(c => c.Platform == "TWITCH")
                .Select(c => new SevenTvEditorGrant(c.Username, c.Id))
                .ToList();
            return SevenTvEditorGrantsResult.Ok(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Editor-Abfrage für 7TV-User {Id} fehlgeschlagen, wird übersprungen.", sevenTvUserId);
            return SevenTvEditorGrantsResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    // Common continuation for both branches of GetChannelStateForTwitchUserAsync: whether emoteSetDto
    // came straight off the primary response or was reloaded via the issue #43 fallback, everything
    // from here on (emote mapping, the v4 AddedToSetAt overlay, the account id, capacity handling)
    // is identical.
    private async Task<SevenTvChannelStateResult> BuildChannelStateResultAsync(
        SevenTvEmoteSetJsonDto emoteSetDto, SevenTvUserRestUserDto? user, CancellationToken cancellationToken)
    {
        var emotes = emoteSetDto.Emotes.Select(SevenTvEmoteJsonMapper.MapDto).ToList();

        // Overlay the real set-entry dates from v4. Null (lookup failed) simply leaves every
        // AddedToSetAt unknown — the sync's correction pass fills the gap on a later resync,
        // which is strictly better than failing the whole channel sync over a date.
        var addedAtByEmoteId = await GetSetEntryAddedAtAsync(emoteSetDto.Id, cancellationToken);
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
        var sevenTvUserId = string.IsNullOrEmpty(user?.Id) ? null : user.Id;

        // 0 reads as "not reported", not as "no slots" — an absent field and a genuine zero are
        // indistinguishable here, and treating either as a capacity of zero would make the UI
        // claim the set is full.
        var capacity = emoteSetDto.Capacity > 0 ? emoteSetDto.Capacity : (int?)null;

        return SevenTvChannelStateResult.Ok(
            new SevenTvChannelState(sevenTvUserId, new SevenTvEmoteSet(emoteSetDto.Id, emotes, capacity)));
    }

    // Reloads a set that the primary response no longer embeds (issue #43). Any non-success status —
    // EnsureSuccessStatusCode covers 404 too — or a malformed body throws HttpRequestException/
    // JsonException/TaskCanceledException, which the caller's try/catch already maps to Unavailable;
    // only a literal JSON `null` body returns normally, and the caller treats that null as Unavailable
    // too. Either way this must never surface as NoActiveEmoteSet — a broken reload says nothing about
    // whether a set is actually active.
    private async Task<SevenTvEmoteSetJsonDto?> FetchEmoteSetAsync(string emoteSetId, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"emote-sets/{emoteSetId}", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SevenTvEmoteSetJsonDto>(
            SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);
    }

    // First entry into the issue #43 fallback path this process has made logs at Information so the
    // rollout's arrival is visible; every later entry — which, once 7TV finishes rolling out, is
    // every channel on every 60s resync tick — drops to Debug for the same reason NoActiveEmoteSet
    // above stays off Information: ~1440 lines a day per channel would drown everything else.
    private void LogFallbackEmoteSetPathEntered(string twitchUserId)
    {
        if (Interlocked.Exchange(ref _fallbackEmoteSetPathLoggedOnce, 1) == 0)
        {
            logger.LogInformation(
                "7TV liefert kein eingebettetes Emote-Set mehr für Twitch-ID {Id} (Ausrollung #43), lade Set separat über emote-sets/{{id}} nach.",
                twitchUserId);
        }
        else
        {
            logger.LogDebug(
                "7TV liefert kein eingebettetes Emote-Set für Twitch-ID {Id}, lade Set separat nach.",
                twitchUserId);
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

    // Resolution order for issue #43: the top-level id — this whole response *is* the requested
    // Twitch connection — and otherwise that same connection found by its exact Twitch user id in
    // the account's connection list. Nothing else, and deliberately so. Every looser candidate
    // belongs to a *different* channel: a non-TWITCH connection can point at an entirely different
    // set, and so can a second TWITCH connection on the same 7TV account. Such a candidate could
    // only ever be reached when the requested connection has no set of its own — which is exactly
    // the case NoActiveEmoteSet exists to report. Taking one instead would make SevenTvSyncService
    // persist a foreign set id and reconcile a foreign channel's emotes into this one, where voting
    // and the 7TV mass delete would then act on it: silent data pollution instead of a visible,
    // truthful failure reason. Same exact-id contract ResolveSevenTvIdentityAsync already holds.
    private static string? ResolveFallbackEmoteSetId(SevenTvUserRestDto dto, string twitchUserId)
    {
        if (IsUsableSevenTvId(dto.EmoteSetId))
        {
            return dto.EmoteSetId;
        }

        var ownConnection = (dto.User?.Connections ?? []).FirstOrDefault(c =>
            c.Platform == "TWITCH" && c.Id == twitchUserId && IsUsableSevenTvId(c.EmoteSetId));

        return ownConnection?.EmoteSetId;
    }

    // 7TV represents "no id" two different ways depending on the endpoint: sometimes a genuine
    // absence (null/empty), sometimes a placeholder sentinel of all-zero characters — proven live for
    // a different lookup on this same client (ResolveSevenTvIdentityAsync's
    // "00000000000000000000000000" placeholder account id, measured 2026-08-31). Both must read as
    // "not present" here, or a sentinel would be mistaken for a real emote-set id and forwarded to
    // GET emote-sets/{id}. Checking "every character is '0'" rather than a fixed-length literal
    // survives 7TV changing the sentinel's length or format.
    //
    // For this particular field the sentinel has not been observed: 62 accounts without an active
    // set, sampled live 2026-09-01, all answered with a plain null emote_set_id (top level and in
    // connections[]). The all-zero branch is therefore unproven defence, not a fix for a known
    // behaviour — the null case, which keeps NoActiveEmoteSet reachable after the rollout, is the
    // measured one.
    private static bool IsUsableSevenTvId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Any(c => c != '0');
}
