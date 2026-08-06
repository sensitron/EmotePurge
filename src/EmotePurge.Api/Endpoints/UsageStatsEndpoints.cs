using System.Globalization;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class UsageStatsEndpoints
{
    public static void MapUsageStatsEndpoints(this WebApplication app)
    {
        // Rate limited at group level: UsageStatsAccessAuthorizationFilter makes two uncached 7TV
        // GraphQL calls for every caller who is not admin/broadcaster/mod, purely to decide access —
        // so even a plain read here can be turned into pressure on someone else's API quota.
        var group = app.MapGroup("/api/channels/{channelName}/usage-stats")
            .RequireAuthorization()
            // Ahead of the authorization filter on purpose — see ChannelNameValidationFilter.
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
            .RequireRateLimiting("ExternalApi");

        group.MapGet("", async (
            string channelName,
            IUsageStatQueryService usageStatQueryService,
            CancellationToken ct) =>
        {
            var stats = await usageStatQueryService.GetUsageStatsAsync(channelName, ct);
            return Results.Ok(stats);
        });

        group.MapGet("/totals", async (
            string channelName,
            string from,
            string to,
            IUsageStatQueryService usageStatQueryService,
            CancellationToken ct) =>
        {
            var rangeError = ValidateRange(from, to, out var fromDate, out var toDate);
            if (rangeError is not null)
            {
                return rangeError;
            }

            var totals = await usageStatQueryService.GetUsageContextAsync(channelName, fromDate, toDate, ct);
            return Results.Ok(totals);
        });

        // The drilldown series (idea A5): one emote, server-side filtered — the whole-channel
        // per-day endpoint above stays the unfiltered debug view it always was. Deliberately on the
        // group's ExternalApi policy rather than a laxer one: the expensive part of a call here is
        // the authorization filter's 7TV lookups, and raising the ceiling would licence more of
        // exactly those. The client caches per (channel, emote, range) instead.
        group.MapGet("/daily", async (
            string channelName,
            string emoteId,
            string from,
            string to,
            IUsageStatQueryService usageStatQueryService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(emoteId))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdEmpty });
            }

            var rangeError = ValidateRange(from, to, out var fromDate, out var toDate);
            if (rangeError is not null)
            {
                return rangeError;
            }

            // Null covers both "unknown id" and "someone else's emote" — a bare 404 either way, so
            // the response does not confirm that a guessed id exists elsewhere.
            var series = await usageStatQueryService.GetDailySeriesAsync(channelName, emoteId, fromDate, toDate, ct);
            return series is null ? Results.NotFound() : Results.Ok(series);
        });

        // The batch twin of /daily: every unarchived emote's days in one response. It exists to keep
        // the atlas's hover readout off the wire entirely — one call per (channel, range) instead of
        // one per emote inspected. Both live on the same ExternalApi policy, and that is the point:
        // the ceiling did not move, the demand did.
        group.MapGet("/series", async (
            string channelName,
            string from,
            string to,
            IUsageStatQueryService usageStatQueryService,
            CancellationToken ct) =>
        {
            var rangeError = ValidateRange(from, to, out var fromDate, out var toDate);
            if (rangeError is not null)
            {
                return rangeError;
            }

            var series = await usageStatQueryService.GetChannelSeriesAsync(channelName, fromDate, toDate, ct);
            return Results.Ok(series);
        });
    }

    /// <summary>
    /// The one from/to ladder shared by /totals and /daily: invalid_date_format → from_after_to →
    /// range_too_large (366 days). Returns the 400 to send, or null when the range is valid.
    /// </summary>
    private static IResult? ValidateRange(string from, string to, out DateOnly fromDate, out DateOnly toDate)
    {
        toDate = default;
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out fromDate) ||
            !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out toDate))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidDateFormat });
        }

        if (fromDate > toDate)
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.FromAfterTo });
        }

        const int maxRangeDays = 366;
        if (toDate.DayNumber - fromDate.DayNumber > maxRangeDays)
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.RangeTooLarge, maxRangeDays });
        }

        return null;
    }
}
