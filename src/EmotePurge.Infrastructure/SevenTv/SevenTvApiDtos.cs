namespace EmotePurge.Infrastructure.SevenTv;

// GQL: POST gql, query { users(query: $q) { id username connections { platform username id } } }
internal sealed class SevenTvGqlUsersResponseDto
{
    public SevenTvGqlDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlDataDto
{
    // Nullable, not defaulted to `[]`: a GraphQL error response omits `users` entirely, and the
    // client needs to tell that apart from a query that genuinely matched nobody.
    public List<SevenTvGqlUserDto>? Users { get; set; }
}

internal sealed class SevenTvGqlUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public List<SevenTvGqlConnectionDto> Connections { get; set; } = [];
}

internal sealed class SevenTvGqlConnectionDto
{
    public string Platform { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

// GQL: userByConnection(platform, id) { id connections { platform id emote_set_id } }
internal sealed class SevenTvGqlUserByConnectionResponseDto
{
    public SevenTvGqlUserByConnectionDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlUserByConnectionDataDto
{
    public SevenTvGqlIdentityUserDto? UserByConnection { get; set; }
}

internal sealed class SevenTvGqlIdentityUserDto
{
    public string Id { get; set; } = string.Empty;
    public List<SevenTvGqlIdentityConnectionDto> Connections { get; set; } = [];
}

internal sealed class SevenTvGqlIdentityConnectionDto
{
    public string Platform { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? EmoteSetId { get; set; }
}

// GQL: emoteSet(id) { owner_id }
internal sealed class SevenTvGqlEmoteSetOwnerResponseDto
{
    public SevenTvGqlEmoteSetOwnerDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlEmoteSetOwnerDataDto
{
    public SevenTvGqlEmoteSetOwnerDto? EmoteSet { get; set; }
}

internal sealed class SevenTvGqlEmoteSetOwnerDto
{
    public string? OwnerId { get; set; }
}

// GQL: user(id) { editor_of { user { connections { platform id username } } } }
internal sealed class SevenTvGqlEditorOfResponseDto
{
    public SevenTvGqlEditorOfDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlEditorOfDataDto
{
    public SevenTvGqlEditorOfUserDto? User { get; set; }
}

internal sealed class SevenTvGqlEditorOfUserDto
{
    public List<SevenTvGqlEditorOfGrantDto> EditorOf { get; set; } = [];
}

internal sealed class SevenTvGqlEditorOfGrantDto
{
    public SevenTvGqlEditorOfOwnerDto? User { get; set; }
}

internal sealed class SevenTvGqlEditorOfOwnerDto
{
    public List<SevenTvGqlConnectionDto> Connections { get; set; } = [];
}

// REST: GET users/twitch/{id}. The response models the Twitch *connection*: its top-level id is
// the Twitch user id, while the 7TV account is the nested user object.
//
// EmoteSetId and User.Connections[].EmoteSetId exist for issue #43: 7TV announced (no date) that it
// will null the embedded EmoteSet object out of this response. Measured live 2026-09-01: the
// top-level EmoteSet is still populated, while the same object inside Connections[] is already null
// everywhere and these plain-id fields stay filled either way. SevenTvApiClient falls back to them
// and reloads the set from GET emote-sets/{id} when EmoteSet is absent; see
// GetChannelStateForTwitchUserAsync.
internal sealed class SevenTvUserRestDto
{
    public SevenTvEmoteSetJsonDto? EmoteSet { get; set; }
    public string? EmoteSetId { get; set; }
    public SevenTvUserRestUserDto? User { get; set; }
}

internal sealed class SevenTvUserRestUserDto
{
    public string Id { get; set; } = string.Empty;
    public List<SevenTvUserRestConnectionDto> Connections { get; set; } = [];
}

// One of the account's linked platform connections. Only ever read for Platform == "TWITCH" — a
// user can have YouTube/Kick connections with entirely different active sets, and syncing those
// would be silent data pollution (issue #43 fallback resolution).
internal sealed class SevenTvUserRestConnectionDto
{
    public string Platform { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? EmoteSetId { get; set; }
}

internal sealed class SevenTvEmoteSetJsonDto
{
    public string Id { get; set; } = string.Empty;

    // Slot limit of the set. Present in the response we already fetch (verified live 2026-08-01);
    // emote_count is deliberately not read — it is emotes.Length of this same payload, so it can
    // never tell us anything the list itself doesn't.
    public int Capacity { get; set; }
    public List<SevenTvEmoteJsonDto> Emotes { get; set; } = [];
}

internal sealed class SevenTvEmoteJsonDto
{
    public string Id { get; set; } = string.Empty;

    // Alias/name as used within this specific emote set (not the emote's global base name).
    public string Name { get; set; } = string.Empty;

    // The payload also carries a `timestamp` per emote, which is deliberately NOT read: it looks
    // like the set entry's added-at but is filled by the v4 compat layer with the emote's *upload*
    // date (proven live 2026-08-03 — 968/968 entries of a large set carried the ULID creation
    // instant of the emote id, while the real added-at from v4 differed by days to over a year).
    public SevenTvEmoteDataJsonDto? Data { get; set; }
}

internal sealed class SevenTvEmoteDataJsonDto
{
    public SevenTvHostJsonDto? Host { get; set; }
}

internal sealed class SevenTvHostJsonDto
{
    public string Url { get; set; } = string.Empty;
    public List<SevenTvFileJsonDto> Files { get; set; } = [];
}

internal sealed class SevenTvFileJsonDto
{
    public string Name { get; set; } = string.Empty;

    // 7TV's own name for the unanimated still of this same file: "4x_static.webp" on an animated
    // emote, and simply a repeat of Name on a still one. Reading it means we never have to decide
    // whether an emote is animated — the payload has already decided. Present on every file of
    // every emote in both wire formats (verified 2026-08-28: 931/931 emotes of the REST set for
    // HandOfBlood, and all 12 files of each captured EventAPI frame under Unit/TestData).
    public string StaticName { get; set; } = string.Empty;
}

// GQL v4 (host-absolute /v4/gql — the shared BaseAddress points at v3):
// emoteSets { emoteSet(id) { emotes(page, perPage) { pageCount items { addedAt emote { id } } } } }
// The only source of the real set-entry added-at; see the comment on SevenTvEmoteJsonDto.
internal sealed class SevenTvGqlSetEntriesResponseDto
{
    public SevenTvGqlSetEntriesDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlSetEntriesDataDto
{
    public SevenTvGqlSetEntriesRootDto? EmoteSets { get; set; }
}

internal sealed class SevenTvGqlSetEntriesRootDto
{
    public SevenTvGqlSetEntriesSetDto? EmoteSet { get; set; }
}

internal sealed class SevenTvGqlSetEntriesSetDto
{
    public SevenTvGqlSetEntriesPageDto? Emotes { get; set; }
}

internal sealed class SevenTvGqlSetEntriesPageDto
{
    public int PageCount { get; set; }
    public List<SevenTvGqlSetEntryDto> Items { get; set; } = [];
}

internal sealed class SevenTvGqlSetEntryDto
{
    public DateTimeOffset? AddedAt { get; set; }
    public SevenTvGqlSetEntryEmoteDto? Emote { get; set; }
}

internal sealed class SevenTvGqlSetEntryEmoteDto
{
    public string Id { get; set; } = string.Empty;
}
