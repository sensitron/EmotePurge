using EmotePurge.Api.Auth;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Everything under /api/admin. Authorization is declared once on the group — every endpoint added
/// here is global-admin-only by construction, so a new one cannot be forgotten. No rate-limit policy:
/// these are DB/Redis-only reads for a single-digit number of allowlisted logins (same call as the
/// pre-existing admin endpoints in ChannelEndpoints).
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin")
            .RequireAuthorization()
            .AddEndpointFilter<GlobalAdminAuthorizationFilter>();

        group.MapGet("/health", async (IWorkerHealthReader healthReader, CancellationToken ct) =>
        {
            // Admin counterpart to the public GET /api/worker/health (Z1 split): same snapshot, same
            // derived statuses — via the shared WorkerHealthStatus so the thresholds cannot drift —
            // but the full operational detail the public badge must not leak.
            var snapshot = await healthReader.ReadAsync(ct);
            if (snapshot is null)
            {
                // The key expired or was never written. Reported as the same shape with everything
                // unknown rather than a shorter error body, so the page renders one layout instead of
                // branching on a second contract. snapshotAvailable is the flag that says which it is.
                return Results.Ok(new
                {
                    snapshotAvailable = false,
                    status = "unknown",
                    isConnected = false,
                    lastMessageReceivedUtc = (DateTime?)null,
                    connectAttemptedUtc = (DateTime?)null,
                    secondsSinceLastMessage = (int?)null,
                    sevenTv = new
                    {
                        status = "unknown",
                        enabled = false,
                        connected = false,
                        lastFrameUtc = (DateTime?)null,
                        lastDispatchUtc = (DateTime?)null,
                        connectAttemptedUtc = (DateTime?)null,
                        secondsSinceLastFrame = (int?)null,
                        desiredChannelCount = (int?)null,
                        desiredSubscriptionCount = (int?)null,
                        unacknowledgedCount = (int?)null,
                        subscriptionLimit = WorkerHealthStatus.SevenTvSubscriptionLimit,
                    },
                    flush = new
                    {
                        consecutiveFailures = (int?)null,
                        lastSuccessUtc = (DateTime?)null,
                        lastRowCount = (int?)null,
                        pendingEmoteCount = (int?)null,
                    },
                });
            }

            var derived = WorkerHealthStatus.Derive(snapshot, DateTime.UtcNow);

            return Results.Ok(new
            {
                snapshotAvailable = true,
                status = derived.Status,
                isConnected = snapshot.IsConnected,
                lastMessageReceivedUtc = snapshot.LastMessageReceivedUtc,
                connectAttemptedUtc = snapshot.ConnectAttemptedUtc,
                secondsSinceLastMessage = derived.SecondsSinceLastMessage,
                sevenTv = new
                {
                    status = derived.SevenTvStatus,
                    enabled = snapshot.SevenTvEnabled,
                    connected = snapshot.SevenTvConnected,
                    lastFrameUtc = snapshot.SevenTvLastFrameUtc,
                    lastDispatchUtc = snapshot.SevenTvLastDispatchUtc,
                    connectAttemptedUtc = snapshot.SevenTvConnectAttemptedUtc,
                    secondsSinceLastFrame = derived.SevenTvSecondsSinceLastFrame,
                    desiredChannelCount = snapshot.SevenTvDesiredChannelCount,
                    desiredSubscriptionCount = snapshot.SevenTvDesiredSubscriptionCount,
                    unacknowledgedCount = snapshot.SevenTvUnacknowledgedCount,
                    subscriptionLimit = WorkerHealthStatus.SevenTvSubscriptionLimit,
                },
                flush = new
                {
                    consecutiveFailures = snapshot.FlushConsecutiveFailures,
                    lastSuccessUtc = snapshot.FlushLastSuccessUtc,
                    lastRowCount = snapshot.FlushLastRowCount,
                    pendingEmoteCount = snapshot.PendingEmoteCount,
                },
            });
        });

        group.MapGet("/channels", async (IAdminChannelQueryService channelQueryService, CancellationToken ct) =>
        {
            // The single global channel list since 2026-07-31: it replaced the narrower
            // GET /api/channels of the overview's admin section, which was removed with that section.
            var channels = await channelQueryService.ListAsync(ct);
            return Results.Ok(channels);
        });

        group.MapGet("/audit-log", async (
            int page,
            int pageSize,
            string? action,
            string? channel,
            string? actor,
            IAuditLogQueryService auditLogQueryService,
            CancellationToken ct) =>
        {
            // Same clamping as the vote-session list and /api/vote-sessions/mine: out-of-range paging
            // is corrected silently rather than rejected, so no new ApiErrorCode is introduced for it
            // (and none has to be mirrored into both locale files).
            var effectivePage = page <= 0 ? 1 : page;
            var effectivePageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 100);

            // Filters are pass-through: an unknown action or channel simply yields an empty page.
            // Channel normalization happens in the query service, next to the matching rule it feeds.
            var filter = new AuditLogFilter(
                NullIfBlank(action),
                NullIfBlank(channel),
                NullIfBlank(actor));

            var result = await auditLogQueryService.ListAsync(effectivePage, effectivePageSize, filter, ct);
            return Results.Ok(result);
        });
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
