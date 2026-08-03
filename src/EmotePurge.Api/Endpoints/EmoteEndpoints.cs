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
        // that look cheap. The two sync-* bookkeeping endpoints override it below.
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

        // The restore counterpart: the browser has already re-added the emotes on 7TV, this call
        // un-archives them here and — its actual reason to exist — writes the emotes.syncRestored
        // audit entry. Without it a restore only ever showed up as an anonymous channel.resync
        // (or, under the resync cooldown, not at all).
        group.MapPost("/sync-restored", async (
            string channelName,
            SyncRestoredRequest request,
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

            var result = await emoteService.MarkRestoredAsync(channelName, request.EmoteIds, actor, ct);
            if (result.NewlyRestoredCount > 0)
            {
                await PublishChannelSyncedAsync(redisPublisher, logger, channelName);
            }

            return Results.Ok(new { restoredCount = result.RestoredCount, notFoundIds = result.NotFoundIds });
        })
        // Same reasoning as sync-deleted: the emotes are already back on 7TV, a dropped call here
        // costs the paper trail and leaves the database stale until the next sync.
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

        // Same audience as active-set: a name collision silently folds the usage of all but one
        // of the colliding emotes into a single counter, so it distorts exactly the numbers those
        // pages show — and fixing it happens on 7TV, which editors can do without being allowed
        // to manage the channel here.
        group.MapGet("/duplicate-names", async (
            string channelName,
            IDuplicateEmoteNameQueryService duplicateEmoteNameQueryService,
            CancellationToken ct) =>
        {
            var duplicates = await duplicateEmoteNameQueryService.GetAsync(channelName, ct);
            return duplicates is null ? Results.NotFound() : Results.Ok(duplicates);
        });
    }

    /// <summary>
    /// Announces "this channel's emote inventory changed" — the same event the worker's sync paths
    /// publish, because the effect on every open page is identical: the database now reflects what
    /// happened on 7TV (archived after a delete, active again after a restore). Published only when
    /// the call actually changed rows (the live sync often got there first, and a no-op must not
    /// make everyone refetch).
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

internal sealed record SyncRestoredRequest(IReadOnlyList<string> EmoteIds);
