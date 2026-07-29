using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

// Weaker than ChannelManagementAuthorizationFilter — additionally admits a channel's 7TV editors.
// Applied as a group filter to five endpoints across two groups (UsageStatsEndpoints: usage-stats,
// usage-stats/totals; EmoteEndpoints: sync-deleted, set-warning, active-set) — not just the two
// usage-stats read endpoints the name suggests.
//
// This deliberately includes sync-deleted, the one write path in that list: a 7TV editor can
// already delete emotes directly via 7TV's own permission system, MarkDeletedAsync only flips
// IsArchived = true (no hard delete, s. CLAUDE.md), and the 1-minute SevenTvPeriodicResyncWorker
// resets that flag anyway on the next full sync if it was set without a matching real 7TV
// deletion — so misuse of this endpoint self-heals within a minute. Anything with real management
// semantics (join/leave, vote sessions, channel config) belongs behind the stricter
// ChannelManagementAuthorizationFilter instead.
public class UsageStatsAccessAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        if (string.IsNullOrEmpty(channelName))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
        }

        var principal = context.HttpContext.User.TryBuildTwitchPrincipal();
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var accessService = context.HttpContext.RequestServices.GetRequiredService<IChannelAccessService>();
        var allowed = await accessService.CanViewUsageStatsAsync(principal, channelName, context.HttpContext.RequestAborted);

        return allowed ? await next(context) : Results.Forbid();
    }
}
