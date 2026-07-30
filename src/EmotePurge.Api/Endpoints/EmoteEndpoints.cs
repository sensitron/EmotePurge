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
        // The whole group carries the strict policy, because the *authorization filter itself* calls
        // 7TV for every non-admin/broadcaster/mod caller — the cost is there even for the endpoints
        // that look cheap. sync-deleted overrides it below.
        var group = app.MapGroup("/api/channels/{channelName}/emotes")
            .RequireAuthorization()
            // Ahead of the authorization filter on purpose — see ChannelNameValidationFilter.
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
            .RequireRateLimiting("ExternalApi");

        group.MapPost("/sync-deleted", async (
            string channelName,
            SyncDeletedRequest request,
            IEmoteService emoteService,
            CancellationToken ct) =>
        {
            if (request.EmoteIds is null || request.EmoteIds.Count == 0)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty });
            }

            var result = await emoteService.MarkDeletedAsync(channelName, request.EmoteIds, ct);
            return Results.Ok(new { archivedCount = result.ArchivedCount, notFoundIds = result.NotFoundIds });
        })
        // Overrides the group's strict policy: this is the one call that must never be dropped. The
        // emotes are already gone from 7TV by the time it runs, so a 429 here leaves the database
        // diverging from reality — and it used to share a 20/min budget with join and the vote
        // endpoints, which several delete batches in one minute could exhaust.
        .RequireRateLimiting("Bookkeeping");

        group.MapGet("/set-warning", async (
            string channelName,
            HttpContext httpContext,
            IEmoteSetOwnershipService emoteSetOwnershipService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            var warning = await emoteSetOwnershipService.CheckAsync(channelName, principal, ct);
            return Results.Ok(warning);
        });

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
            var channel = await channelService.GetByNameAsync(channelName, ct);
            return channel is null
                ? Results.NotFound()
                : Results.Ok(new { activeEmoteSetId = channel.ActiveEmoteSetId });
        });
    }
}

internal sealed record SyncDeletedRequest(IReadOnlyList<string> EmoteIds);
