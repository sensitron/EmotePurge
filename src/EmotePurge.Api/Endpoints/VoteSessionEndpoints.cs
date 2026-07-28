using System.Security.Claims;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class VoteSessionEndpoints
{
    public static void MapVoteSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/channels/{channelName}/vote-sessions").RequireAuthorization();

        group.MapPost("", async (
            string channelName,
            CreateVoteSessionRequest request,
            IVoteSessionService voteSessionService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.VoteSessionTitleEmpty });
            }

            if (request.AllowedVoterRoles == 0)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.VoteSessionRolesEmpty });
            }

            if (request.AllowedVoterRoles.HasFlag(AllowedRoles.VIPs))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.VipsNotSupported });
            }

            if (request.StartedAt is { } startedAt && startedAt > DateTime.UtcNow)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.StartedAtInFuture });
            }

            var session = await voteSessionService.CreateAsync(channelName, request.Title, request.AllowedVoterRoles, request.StartedAt, ct);
            if (session is null)
            {
                return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotJoined });
            }

            return Results.Ok(new VoteSessionSummaryDto(session.Id, session.Title, session.AllowedVoterRoles, session.IsActive, session.StartedAt, session.EndedAt));
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>();

        group.MapPost("/{sessionId:long}/end", async (
            string channelName,
            long sessionId,
            IVoteSessionService voteSessionService,
            CancellationToken ct) =>
        {
            var session = await voteSessionService.EndAsync(channelName, sessionId, ct);
            if (session is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new VoteSessionSummaryDto(session.Id, session.Title, session.AllowedVoterRoles, session.IsActive, session.StartedAt, session.EndedAt));
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>();

        group.MapDelete("/{sessionId:long}", async (
            string channelName,
            long sessionId,
            IVoteSessionService voteSessionService,
            CancellationToken ct) =>
        {
            var deleted = await voteSessionService.DeleteAsync(channelName, sessionId, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>();

        group.MapGet("", async (
            string channelName,
            int page,
            int pageSize,
            HttpContext httpContext,
            IVoteSessionQueryService voteSessionQueryService,
            IChannelAccessService channelAccessService,
            IVoteEligibilityService voteEligibilityService,
            CancellationToken ct) =>
        {
            if (!ChannelNameValidation.IsValid(channelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            var effectivePage = page <= 0 ? 1 : page;
            var effectivePageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 100);

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            // Managers (admin/broadcaster/live-moderator) see every session unfiltered — same standing
            // as the "Neue Abstimmung erstellen" section — and get true DB-level pagination since no
            // per-session filter has to run first.
            if (await channelAccessService.CanManageChannelAsync(principal, channelName, ct))
            {
                var paged = await voteSessionQueryService.ListSessionsPagedAsync(channelName, effectivePage, effectivePageSize, ct);
                return Results.Ok(paged);
            }

            // Everyone else only sees sessions they're personally part of the audience for
            // (VoteEligibilityService.EvaluateAudienceAsync, same check used to gate the results page) —
            // a session a viewer has no rights to shouldn't even reveal its existence to them. Filter-
            // then-paginate (in memory, over the full per-channel list) rather than paginate-then-filter:
            // DB-side Skip/Take before filtering could return a page that shrinks to fewer than
            // pageSize (or zero) items after eligibility is applied even though more eligible sessions
            // exist further down — breaking the pagination contract and making TotalCount/TotalPages
            // meaningless. A channel's vote-session history is small (one broadcaster's own history,
            // not a global table), so the in-memory pass stays cheap.
            var allSessions = await voteSessionQueryService.ListSessionsAsync(channelName, ct);
            var visibleSessions = new List<VoteSessionSummaryDto>();
            foreach (var session in allSessions)
            {
                var eligibility = await voteEligibilityService.EvaluateAudienceAsync(principal, channelName, session.Id, ct);
                if (eligibility == VoteEligibilityResult.Allowed)
                {
                    visibleSessions.Add(session);
                }
            }

            var pageItems = visibleSessions.Skip((effectivePage - 1) * effectivePageSize).Take(effectivePageSize).ToList();
            return Results.Ok(new PagedResult<VoteSessionSummaryDto>(pageItems, effectivePage, effectivePageSize, visibleSessions.Count));
        });

        group.MapGet("/{sessionId:long}/results", async (
            string channelName,
            long sessionId,
            HttpContext httpContext,
            IVoteSessionQueryService voteSessionQueryService,
            CancellationToken ct) =>
        {
            // VoteAudienceFilter already required an authenticated, in-audience principal to reach this
            // handler — anonymous viewing was removed (see decision log). MyVote is filled in from the
            // now-guaranteed-present principal.
            var viewerTwitchUserId = httpContext.User.TryBuildTwitchPrincipal()!.TwitchUserId;
            var results = await voteSessionQueryService.GetResultsAsync(channelName, sessionId, viewerTwitchUserId, ct);
            return results is null ? Results.NotFound() : Results.Ok(results);
        })
        .AddEndpointFilter<VoteAudienceFilter>();

        group.MapPost("/{sessionId:long}/votes", async (
            string channelName,
            long sessionId,
            CastVoteRequest request,
            IVoteSessionService voteSessionService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.EmoteId))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdEmpty });
            }

            if (request.Type is not (VoteType.Keep or VoteType.Delete))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidVoteType });
            }

            // VoteEligibilityFilter already required an authenticated principal to reach this handler.
            var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var (result, vote) = await voteSessionService.CastVoteAsync(channelName, sessionId, request.EmoteId, twitchUserId, request.Type, ct);

            return result switch
            {
                VoteCastResult.Success => Results.Ok(new { voteId = vote!.Id, emoteId = vote.EmoteId, type = vote.Type, updatedAt = vote.UpdatedAt }),
                VoteCastResult.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                VoteCastResult.SessionNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.VoteSessionNotFound }),
                VoteCastResult.SessionEnded => Results.Conflict(new { errorCode = ApiErrorCodes.VoteSessionEnded }),
                VoteCastResult.EmoteNotEligible => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteNotEligible }),
                _ => Results.Problem()
            };
        })
        .AddEndpointFilter<VoteEligibilityFilter>()
        .RequireRateLimiting("ExpensiveOps");

        group.MapDelete("/{sessionId:long}/votes/{emoteId}", async (
            string channelName,
            long sessionId,
            string emoteId,
            IVoteSessionService voteSessionService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // VoteEligibilityFilter already required an authenticated principal to reach this handler.
            var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var result = await voteSessionService.RetractVoteAsync(channelName, sessionId, emoteId, twitchUserId, ct);

            return result switch
            {
                VoteCastResult.Success => Results.NoContent(),
                VoteCastResult.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                VoteCastResult.SessionNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.VoteSessionNotFound }),
                VoteCastResult.SessionEnded => Results.Conflict(new { errorCode = ApiErrorCodes.VoteSessionEnded }),
                _ => Results.Problem()
            };
        })
        .AddEndpointFilter<VoteEligibilityFilter>()
        .RequireRateLimiting("ExpensiveOps");

        // Deliberately NOT nested under /api/channels/{channelName}/... — a user's voting history spans
        // every channel they've ever voted in, so there's no single channelName route value to key off.
        app.MapGet("/api/vote-sessions/mine", async (
            int page,
            int pageSize,
            HttpContext httpContext,
            IVoteSessionQueryService voteSessionQueryService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var effectivePage = page <= 0 ? 1 : page;
            var effectivePageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 100);

            var result = await voteSessionQueryService.ListMyVoteSessionsAsync(principal.TwitchUserId, effectivePage, effectivePageSize, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization();
    }
}

internal sealed record CreateVoteSessionRequest(string Title, AllowedRoles AllowedVoterRoles, DateTime? StartedAt = null);
internal sealed record CastVoteRequest(string EmoteId, VoteType Type);
