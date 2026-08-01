using System.Text.Json;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Infrastructure.SevenTv;

public static class SevenTvEmoteJsonMapper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    internal static SevenTvEmote MapDto(SevenTvEmoteJsonDto dto) =>
        new(dto.Id, dto.Name, BuildImageUrl(dto.Data?.Host), ToUtc(dto.Timestamp));

    // Serves both the REST sync and the EventAPI dispatch parser, so a missing field is normal
    // rather than exceptional: 0 covers "absent" and 7TV's own placeholder alike, and both read as
    // "unknown". Never as the epoch — that would date every such emote to 1970 and let it look like
    // the oldest thing in the set.
    private static DateTime? ToUtc(long unixMilliseconds) =>
        unixMilliseconds <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;

    private static string BuildImageUrl(SevenTvHostJsonDto? host)
    {
        if (host is null || host.Files.Count == 0)
        {
            return string.Empty;
        }

        var chosen = host.Files.FirstOrDefault(f => f.Name == "2x.webp")
            ?? host.Files.FirstOrDefault(f => f.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            ?? host.Files[0];

        return $"https:{host.Url}/{chosen.Name}";
    }
}
