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
                return Results.BadRequest(new { error = "Invalid Twitch channel name." });
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
                return Results.BadRequest(new { error = "Invalid Twitch channel name." });
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
                return Results.BadRequest(new { error = "Invalid Twitch channel name." });
            }

            var removed = await channelService.LeaveAsync(channelName, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>();
    }
}
