namespace EmotePurge.Infrastructure.SevenTv;

// GQL: POST gql, query { users(query: $q) { id username connections { platform username id } } }
internal sealed class SevenTvGqlUsersResponseDto
{
    public SevenTvGqlDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlDataDto
{
    public List<SevenTvGqlUserDto> Users { get; set; } = [];
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
internal sealed class SevenTvUserRestDto
{
    public SevenTvEmoteSetJsonDto? EmoteSet { get; set; }
    public SevenTvUserRestUserDto? User { get; set; }
}

internal sealed class SevenTvUserRestUserDto
{
    public string Id { get; set; } = string.Empty;
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

    // When the emote was added to the set, Unix milliseconds. A property of the *set entry*, which
    // is what makes it retroactively correct for emotes we have been tracking for months — unlike a
    // "first time we saw it" stamp, which would only ever be right going forward.
    public long Timestamp { get; set; }
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
}
