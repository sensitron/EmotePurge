using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The read side of "Fremde Kanäle als Import-Quelle" (spec 2026-09-09): any logged-in user can read
/// any Twitch channel's active 7TV emote set, no role in that channel required. That is the whole
/// point of the feature, and the reason this group carries no
/// <c>UsageStatsAccessAuthorizationFilter</c> — the nearest existing precedent for "logged in,
/// cross-channel" is <see cref="LiveEndpoints.MapLiveEndpoints"/>'s two routes, not the emotes group
/// in <see cref="EmoteEndpoints"/>.
/// </summary>
public static class SevenTvEndpoints
{
    public static void MapSevenTvEndpoints(this WebApplication app)
    {
        // No /api/seventv/... group existed before this spec. Filter order here is a tested contract
        // (spec section 4, AK 14): UseAuthentication/UseAuthorization and UseRateLimiter are
        // middleware and run before any endpoint filter regardless of registration order below — so
        // an invalid channel name that has already exhausted the rate-limit budget answers 429, not
        // 400. ChannelNameValidationFilter still runs first among the *endpoint* filters, matching
        // every other channel-scoped group.
        var group = app.MapGroup("/api/seventv/channels/{channelName}/emotes")
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.ForeignEmoteLookup);

        group.MapGet("", async (
            string channelName,
            IForeignEmoteSetService foreignEmoteSetService,
            CancellationToken ct) =>
        {
            var result = await foreignEmoteSetService.GetForeignEmoteSetAsync(channelName, ct);

            // Mirrors the state table in spec section 5 one-to-one. SevenTvRateLimited and
            // SevenTvUnavailable deliberately share a branch and a code: the table has one row for
            // "7TV nicht erreichbar / 429", because the caller cannot act on the two any differently.
            return result.Status switch
            {
                ForeignEmoteSetLookupStatus.Ok => Results.Ok(result.EmoteSet),
                ForeignEmoteSetLookupStatus.ChannelNotOnTwitch =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotOnTwitch }),
                ForeignEmoteSetLookupStatus.TwitchUnavailable => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelTwitchUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                ForeignEmoteSetLookupStatus.NoSevenTvAccount =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ForeignChannelNoSevenTvAccount }),
                ForeignEmoteSetLookupStatus.NoActiveEmoteSet =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ForeignChannelNoActiveEmoteSet }),
                ForeignEmoteSetLookupStatus.SevenTvUnavailable or ForeignEmoteSetLookupStatus.SevenTvRateLimited => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result), result.Status, "Unbekannter ForeignEmoteSetLookupStatus.")
            };
        });
    }
}
