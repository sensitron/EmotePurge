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
        new(dto.Id, dto.Name, BuildImageUrl(dto.Data?.Host));

    // Used by the 7TV EventAPI dispatch parser (Worker), which only has raw JsonElements, not the typed DTOs.
    public static SevenTvEmote MapFromJsonElement(JsonElement emoteJson)
    {
        var dto = emoteJson.Deserialize<SevenTvEmoteJsonDto>(JsonOptions)
            ?? throw new JsonException("7TV emote JSON konnte nicht deserialisiert werden.");
        return MapDto(dto);
    }

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
