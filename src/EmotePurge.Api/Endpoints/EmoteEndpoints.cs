using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
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
            HttpContext httpContext,
            IEmoteService emoteService,
            IRedisPublisher redisPublisher,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.EmoteIds is null || request.EmoteIds.Count == 0)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty });
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await emoteService.MarkDeletedAsync(channelName, request.EmoteIds, actor, ct);
            if (result.NewlyArchivedCount > 0)
            {
                await PublishChannelSyncedAsync(redisPublisher, logger, channelName);
            }

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
        // the channel at all. The slot budget and the tracking start ride along on this same call for
        // exactly that reason: both are for the same audience, and both pages already fetch this.
        group.MapGet("/active-set", async (
            string channelName,
            IEmoteSetStatusService emoteSetStatusService,
            CancellationToken ct) =>
        {
            var status = await emoteSetStatusService.GetAsync(channelName, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });
    }

    /// <summary>
    /// Announces "this channel's emote inventory changed" — the same event the worker's sync paths
    /// publish, because the effect on every open page is identical: emotes that are gone from 7TV
    /// are now archived here too. Published only when this call actually archived something (the
    /// live sync often got there first, and a no-op must not make everyone refetch).
    /// <para>
    /// In the endpoint rather than in EmoteService, exactly like the vote event: the notification
    /// belongs to the request that caused it, and IRedisPublisher in a handler is explicitly
    /// allowed by rule 4. Failure is logged and swallowed — the archiving is committed and the
    /// response must not change because Redis hiccuped.
    /// </para>
    /// </summary>
    private static async Task PublishChannelSyncedAsync(
        IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName)
    {
        try
        {
            // No request token on purpose: the write is committed, so a client that hung up right
            // after the delete must not cost every *other* viewer their update.
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.ChannelSynced, ChannelName.Normalize(channelName)).Serialize());
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.",
                LiveEvents.ChannelSynced, channelName);
        }
    }
}

internal sealed record SyncDeletedRequest(IReadOnlyList<string> EmoteIds);
