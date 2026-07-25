using System.Text.Json;

namespace EmotePurge.Infrastructure.Twitch;

internal static class TwitchJsonOptions
{
    public static readonly JsonSerializerOptions Value = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}
