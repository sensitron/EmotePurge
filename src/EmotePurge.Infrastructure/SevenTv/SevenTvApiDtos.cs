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

// REST: GET users/twitch/{id}
internal sealed class SevenTvUserRestDto
{
    public string Id { get; set; } = string.Empty;
    public SevenTvEmoteSetJsonDto? EmoteSet { get; set; }
}

internal sealed class SevenTvEmoteSetJsonDto
{
    public string Id { get; set; } = string.Empty;
    public List<SevenTvEmoteJsonDto> Emotes { get; set; } = [];
}

internal sealed class SevenTvEmoteJsonDto
{
    public string Id { get; set; } = string.Empty;

    // Alias/name as used within this specific emote set (not the emote's global base name).
    public string Name { get; set; } = string.Empty;
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
