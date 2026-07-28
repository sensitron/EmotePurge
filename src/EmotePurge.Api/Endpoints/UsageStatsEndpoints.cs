using System.Globalization;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class UsageStatsEndpoints
{
    public static void MapUsageStatsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/channels/{channelName}/usage-stats")
            .RequireAuthorization()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>();

        group.MapGet("", async (
            string channelName,
            IUsageStatQueryService usageStatQueryService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

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
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

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

            var totals = await usageStatQueryService.GetUsageTotalsAsync(channelName, fromDate, toDate, ct);
            return Results.Ok(totals);
        });
    }
}
