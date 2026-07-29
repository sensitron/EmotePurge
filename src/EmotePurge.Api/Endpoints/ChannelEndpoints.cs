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
        });

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
        .RequireRateLimiting("ExpensiveOps");

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
