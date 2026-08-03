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

    // AddedToSetAt stays at its null default here: the payload's own timestamp field is the
    // emote's upload date, not the set-entry date (see SevenTvEmoteJsonDto). The REST path
    // overlays the real added-at from v4 afterwards; the dispatch path knows it only for pushes.
    internal static SevenTvEmote MapDto(SevenTvEmoteJsonDto dto) =>
        new(dto.Id, dto.Name, BuildImageUrl(dto.Data?.Host));

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
