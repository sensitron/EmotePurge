using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/channels").RequireAuthorization();

        group.MapGet("/{channelName}", async (
            string channelName,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            var channel = await channelService.GetByNameAsync(channelName, ct);
            return channel is null
                ? Results.NotFound()
                : Results.Ok(new { channelId = channel.Id, channelName = channel.ChannelName, channel.IsBotActive, channel.ActiveEmoteSetId });
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>();

        // Deliberately behind no authorization filter of its own: this endpoint *reports* whether the
        // caller would pass those filters, so gating it with one would make it answer only for people
        // who already know the answer. It replaces four separate UI probes that each called a real
        // endpoint and read its success/failure as a permission bit — a navigation to
        // /channels/{c}/usage-stats used to cost four requests, three of them redundant
        // GetUsageTotalsAsync calls, before the page showed anything.
        group.MapGet("/{channelName}/permissions", async (
            string channelName,
            HttpContext httpContext,
            IChannelAccessService channelAccessService,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var canManage = await channelAccessService.CanManageChannelAsync(principal, channelName, ct);
            // Short-circuited on purpose: CanViewUsageStatsAsync is a superset of CanManageChannelAsync
            // and its extra branch is the 7TV editor lookup. Calling it unconditionally would drag 7TV
            // into the request path for every manager, who never needs it.
            var canViewUsageStats = canManage || await channelAccessService.CanViewUsageStatsAsync(principal, channelName, ct);
            var channel = await channelService.GetByNameAsync(channelName, ct);

            return Results.Ok(new ChannelPermissionsDto(
                canManage,
                canViewUsageStats,
                channelAccessService.IsGlobalAdmin(principal),
                IsTracked: channel is not null,
                IsBotActive: channel?.IsBotActive ?? false));
        })
        // Both access checks can reach Helix or 7TV on a cache miss, same reasoning as the
        // usage-stats group.
        .RequireRateLimiting("ExternalApi");

        group.MapGet("", async (
            IChannelService channelService,
            CancellationToken ct) =>
        {
            var channels = await channelService.ListAllAsync(ct);
            return Results.Ok(channels.Select(c => new
            {
                channelId = c.Id,
                channelName = c.ChannelName,
                c.IsBotActive,
                c.TwitchChannelId,
                c.CreatedAt
            }));
        })
        .AddEndpointFilter<GlobalAdminAuthorizationFilter>();

        group.MapGet("/mine", async (
            HttpContext httpContext,
            IMyChannelsService myChannelsService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var result = await myChannelsService.GetMyChannelsAsync(principal, ct);
            return Results.Ok(result);
        })
        // The heaviest endpoint in the app per incoming request: up to ten Helix calls plus a 7TV
        // identity resolve plus an editor lookup, none of them cached. Unlimited, it let any logged-in
        // account exhaust the app-wide Twitch quota, at which point Helix returns nothing for
        // everyone and every moderator of every channel silently loses their permissions.
        .RequireRateLimiting("ExternalApi");

        group.MapPost("/{channelName}/join", async (
            string channelName,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            var channel = await channelService.JoinAsync(channelName, ct);
            return Results.Ok(new { channelId = channel.Id, channelName = channel.ChannelName, channel.IsBotActive });
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        .RequireRateLimiting("ExternalApi");

        group.MapDelete("/{channelName}", async (
            string channelName,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            // Deactivates the bot and keeps all history — see ChannelService.LeaveAsync for why this
            // must not be a delete. The destructive variant lives behind /purge below.
            var deactivated = await channelService.LeaveAsync(channelName, ct);
            return deactivated ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>();

        // Admin-only and deliberately without any UI: this is the only way to irreversibly remove a
        // channel with its emotes, usage history, vote sessions and votes. Not behind
        // ChannelManagementAuthorizationFilter, because that admits moderators — and a positively
        // cached mod status survives a /unmod by up to ten minutes, which is exactly the window in
        // which a just-removed moderator could have destroyed a channel's entire history.
        group.MapDelete("/{channelName}/purge", async (
            string channelName,
            IChannelService channelService,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            var purged = await channelService.PurgeAsync(channelName, ct);
            if (purged)
            {
                logger.LogWarning("Channel {Channel} wurde per Admin-Purge samt Historie gelöscht.", channelName);
            }

            return purged ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<GlobalAdminAuthorizationFilter>();
    }
}

/// <summary>
/// What the caller may do with one channel. <c>CanViewUsageStats</c> is a superset of
/// <c>CanManage</c> (it additionally admits the channel's 7TV editors), so a consumer that only wants
/// "may see the usage tab" must read that field and not <c>CanManage</c>.
/// <para>
/// <c>IsBotActive</c> is part of this payload rather than a second call because leaving a channel now
/// only deactivates it: without it the workspace could not tell a healthy channel from one that has
/// silently stopped collecting, and the reactivate button would have nothing to key off.
/// </para>
/// <para>
/// No <c>IsSevenTvEditor</c> field, despite the review's sketch: nothing consumes it today, and
/// computing it would defeat the short-circuit above and put a 7TV call in every manager's request.
/// Add it together with its first consumer.
/// </para>
/// </summary>
internal sealed record ChannelPermissionsDto(
    bool CanManage,
    bool CanViewUsageStats,
    bool IsGlobalAdmin,
    bool IsTracked,
    bool IsBotActive);
