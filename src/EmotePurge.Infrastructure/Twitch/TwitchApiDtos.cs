namespace EmotePurge.Infrastructure.Twitch;

// POST id.twitch.tv/oauth2/token (form-urlencoded request, JSON response)
internal sealed class TwitchTokenResponseDto
{
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
}

// GET api.twitch.tv/helix/users
internal sealed class TwitchGetUsersResponseDto
{
    public List<TwitchUserDto> Data { get; set; } = [];
}

internal sealed class TwitchUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

// GET api.twitch.tv/helix/moderation/channels
internal sealed class TwitchGetModeratedChannelsResponseDto
{
    public List<TwitchModeratedChannelDto> Data { get; set; } = [];
    public TwitchPaginationDto? Pagination { get; set; }
}

internal sealed class TwitchModeratedChannelDto
{
    public string BroadcasterLogin { get; set; } = string.Empty;
    public string BroadcasterId { get; set; } = string.Empty;
}

internal sealed class TwitchPaginationDto
{
    public string? Cursor { get; set; }
}

// GET api.twitch.tv/helix/subscriptions/user
internal sealed class TwitchGetUserSubscriptionResponseDto
{
    public List<TwitchUserSubscriptionDto> Data { get; set; } = [];
}

internal sealed class TwitchUserSubscriptionDto
{
    public string BroadcasterId { get; set; } = string.Empty;
}
