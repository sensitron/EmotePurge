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
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
                !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
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

            var totals = await usageStatQueryService.GetUsageContextAsync(channelName, fromDate, toDate, ct);
            return Results.Ok(totals);
        });
    }
}
