using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class EmoteEndpoints
{
    public static void MapEmoteEndpoints(this WebApplication app)
    {
        // UsageStatsAccessAuthorizationFilter (not ChannelManagementAuthorizationFilter) applies to the
        // whole group: a channel's 7TV editors can legitimately delete/view via 7TV's own permission
        // system, so they must be admitted here too, not just channel managers.
        var group = app.MapGroup("/api/channels/{channelName}/emotes")
            .RequireAuthorization()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>();

        group.MapPost("/sync-deleted", async (
            string channelName,
            SyncDeletedRequest request,
            IEmoteService emoteService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { error = "Invalid Twitch channel name." });
            }

            if (request.EmoteIds is null || request.EmoteIds.Count == 0)
            {
                return Results.BadRequest(new { error = "EmoteIds must not be empty." });
            }

            var result = await emoteService.MarkDeletedAsync(channelName, request.EmoteIds, ct);
            return Results.Ok(new { archivedCount = result.ArchivedCount, notFoundIds = result.NotFoundIds });
        })
        .RequireRateLimiting("ExpensiveOps");

        group.MapGet("/set-warning", async (
            string channelName,
            HttpContext httpContext,
            IEmoteSetOwnershipService emoteSetOwnershipService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { error = "Invalid Twitch channel name." });
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            var warning = await emoteSetOwnershipService.CheckAsync(channelName, principal?.TwitchUserId, principal?.AccessToken, ct);
            return Results.Ok(warning);
        })
        .RequireRateLimiting("ExpensiveOps");

        // Deliberately separate from GET /api/channels/{channelName} (which stays management-only,
        // since it also backs the join-status/leave-button check): the mass-delete panel needs the
        // active set id to render its "Löschen" button, and 7TV editors — who can legitimately delete
        // via 7TV's own permission system — must be able to see it despite not being allowed to manage
        // the channel at all.
        group.MapGet("/active-set", async (
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
                : Results.Ok(new { activeEmoteSetId = channel.ActiveEmoteSetId });
        });
    }
}

internal sealed record SyncDeletedRequest(IReadOnlyList<string> EmoteIds);
