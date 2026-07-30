using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class WorkerHealthEndpoints
{
    // Mirrors TwitchConnectionWatchdog's 5-minute threshold. A literal because the Api and the Worker
    // share no code here beyond the snapshot contract.
    private const int StaleAfterSeconds = 300;

    // Mirrors the 7TV event client's heartbeat watchdog (3 × ~45s heartbeat interval, plus slack).
    // Same literal-by-design reasoning as StaleAfterSeconds above.
    private const int SevenTvStaleAfterSeconds = 150;

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

            var secondsSinceLastMessage = snapshot.LastMessageReceivedUtc is { } lastMessage
                ? (int)(DateTime.UtcNow - lastMessage).TotalSeconds
                : (int?)null;

            // Deriving the status from isConnected alone let the endpoint report "connected" while
            // nothing was arriving — the flag can lag reality (silent freeze) and, before the
            // recreate path reset it, could stay true on a client that had already been discarded.
            // "stale" therefore also covers "connected, but no chat data for a while". Falls back to
            // the connect attempt while no message has ever arrived, so a worker that just started
            // isn't reported as stale for the few seconds before the first chat line.
            var quietSince = snapshot.LastMessageReceivedUtc ?? snapshot.ConnectAttemptedUtc;
            var quietForSeconds = quietSince is { } since ? (int)(DateTime.UtcNow - since).TotalSeconds : (int?)null;
            var status = snapshot.IsConnected switch
            {
                false => "disconnected",
                true when quietForSeconds is null or > StaleAfterSeconds => "stale",
                true => "connected",
            };

            // 7TV staleness keys off frames (heartbeats arrive every ~45s), never off dispatches —
            // a channel whose emote set nobody edits gets no dispatches for days and would
            // otherwise read as permanently stale. "disabled" keeps a deliberately switched-off
            // event path distinguishable from a broken one.
            var sevenTvQuietSince = snapshot.SevenTvLastFrameUtc ?? snapshot.SevenTvConnectAttemptedUtc;
            var sevenTvQuietForSeconds = sevenTvQuietSince is { } sevenTvSince
                ? (int)(DateTime.UtcNow - sevenTvSince).TotalSeconds
                : (int?)null;
            var sevenTvStatus = snapshot switch
            {
                { SevenTvEnabled: false } => "disabled",
                { SevenTvConnected: false } => "disconnected",
                _ when sevenTvQuietForSeconds is null or > SevenTvStaleAfterSeconds => "stale",
                _ => "connected",
            };

            return Results.Ok(new
            {
                status,
                snapshot.IsConnected,
                snapshot.LastMessageReceivedUtc,
                secondsSinceLastMessage,
                sevenTv = new
                {
                    status = sevenTvStatus,
                    snapshot.SevenTvLastDispatchUtc,
                    secondsSinceLastFrame = sevenTvQuietForSeconds,
                },
            });
        });
    }
}
