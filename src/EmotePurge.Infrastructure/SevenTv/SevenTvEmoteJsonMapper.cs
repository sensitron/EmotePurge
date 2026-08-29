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

    /// <summary>
    /// The one image url stored per emote: the 4x <em>still</em>, which every surface but the
    /// sidecar draws.
    /// </summary>
    /// <remarks>
    /// 4x rather than 2x because the atlas cell is 64 CSS px, and only a 128 px source stays sharp
    /// at a device pixel ratio of 2. That costs nothing here, because the still is what we ask for:
    /// on an animated emote the plain 4x.webp carries every frame. Measured 2026-08-28 across
    /// HandOfBlood's 931 emotes — 630 of them animated, averaging 133 KB at 2x and reaching 1.2 MB
    /// for a single 64 px cell — the set was 73 MB as 2x.webp and is 15 MB as 4x_static.webp.
    ///
    /// The animation is not dropped, it moves to where one emote is shown at a time: the frontend
    /// derives the animated url from this one for the sidecar and the readout line (animatedEmoteUrl
    /// in web/src/app/shared/emotes/emote-url.ts), which is why the "_static" marker in this string
    /// is load-bearing rather than incidental.
    /// </remarks>
    private static string BuildImageUrl(SevenTvHostJsonDto? host)
    {
        if (host is null || host.Files.Count == 0)
        {
            return string.Empty;
        }

        var chosen = host.Files.FirstOrDefault(f => f.Name == "4x.webp")
            ?? host.Files.FirstOrDefault(f => f.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            ?? host.Files[0];

        // Falling back to Name keeps a payload that ever omits static_name serving a real image
        // rather than a url ending in a bare slash. No captured payload does (see the DTO), so
        // nothing exercises this branch — it is a guard, not a behaviour.
        var fileName = chosen.StaticName.Length > 0 ? chosen.StaticName : chosen.Name;

        return $"https:{host.Url}/{fileName}";
    }
}
