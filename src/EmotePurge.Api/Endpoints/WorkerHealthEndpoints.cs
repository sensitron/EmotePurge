using EmotePurge.Api.Health;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class WorkerHealthEndpoints
{
    public static void MapWorkerHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/worker/health", async (IWorkerHealthReader healthReader, CancellationToken ct) =>
        {
            // The worker publishes the snapshot to Redis with a TTL (see WorkerHealthPublisher), so Api
            // and Worker never talk directly. If the worker is gone or its publisher is wedged, the key
            // simply expires — and that absence is itself the signal. Reading it goes through
            // IWorkerHealthReader rather than IConnectionMultiplexer directly: this was the only place
            // in the Api that reached past its own service layer into infrastructure, and it meant the
            // wire format was declared once here and once in the worker.
            var snapshot = await healthReader.ReadAsync(ct);
            if (snapshot is null)
            {
                return Results.Ok(new { status = "unknown", reasonCode = ApiErrorCodes.NoHealthData });
            }

            // Status derivation and its thresholds live in WorkerHealthStatus, shared with the
            // admin endpoint. This response deliberately stays minimal and unauthenticated: it
            // feeds the header badge every visitor polls, so it exposes no operational detail
            // (subscription counts, flush failures) — those are admin-only (Z1 split).
            var derived = WorkerHealthStatus.Derive(snapshot, DateTime.UtcNow);

            return Results.Ok(new
            {
                status = derived.Status,
                snapshot.IsConnected,
                snapshot.LastMessageReceivedUtc,
                secondsSinceLastMessage = derived.SecondsSinceLastMessage,
                sevenTv = new
                {
                    status = derived.SevenTvStatus,
                    snapshot.SevenTvLastDispatchUtc,
                    secondsSinceLastFrame = derived.SevenTvSecondsSinceLastFrame,
                },
            });
        });
    }
}
