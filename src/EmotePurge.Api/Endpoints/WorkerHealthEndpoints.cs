using System.Text.Json;
using EmotePurge.Api.Validation;
using StackExchange.Redis;

namespace EmotePurge.Api.Endpoints;

public static class WorkerHealthEndpoints
{
    public static void MapWorkerHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/worker/health", async (IConnectionMultiplexer redis) =>
        {
            // Redis-Key wird vom Worker periodisch mit TTL geschrieben (s. WorkerHealthPublisher) —
            // Api/Worker kommunizieren dadurch nicht direkt miteinander. Läuft der Worker nicht (mehr)
            // oder hängt der Health-Publisher, läuft der Key einfach ab; das ist dann selbst das Signal.
            var value = await redis.GetDatabase().StringGetAsync("worker:health:twitch");
            if (value.IsNullOrEmpty)
            {
                return Results.Ok(new { status = "unknown", reasonCode = ApiErrorCodes.NoHealthData });
            }

            var payload = JsonSerializer.Deserialize<WorkerHealthPayload>((string)value!, JsonSerializerOptions.Web);
            if (payload is null)
            {
                return Results.Ok(new { status = "unknown", reasonCode = ApiErrorCodes.HealthDataUnreadable });
            }

            var secondsSinceLastMessage = payload.LastMessageReceivedUtc is { } lastMessage
                ? (int)(DateTime.UtcNow - lastMessage).TotalSeconds
                : (int?)null;

            // Deriving the status from isConnected alone let the endpoint report "connected" while
            // nothing was arriving — the flag can lag reality (silent freeze) and, before the
            // recreate path reset it, could stay true on a client that had already been discarded.
            // "stale" therefore also covers "connected, but no chat data for a while". Falls back to
            // the connect attempt while no message has ever arrived, so a worker that just started
            // isn't reported as stale for the few seconds before the first chat line. Mirrors
            // TwitchConnectionWatchdog's 5-minute threshold; a literal because Api and Worker share
            // no code here.
            const int staleAfterSeconds = 300;
            var quietSince = payload.LastMessageReceivedUtc ?? payload.ConnectAttemptedUtc;
            var quietForSeconds = quietSince is { } since ? (int)(DateTime.UtcNow - since).TotalSeconds : (int?)null;
            var status = payload.IsConnected switch
            {
                false => "disconnected",
                true when quietForSeconds is null or > staleAfterSeconds => "stale",
                true => "connected",
            };

            return Results.Ok(new
            {
                status,
                payload.IsConnected,
                payload.LastMessageReceivedUtc,
                secondsSinceLastMessage,
            });
        });
    }
}

internal sealed record WorkerHealthPayload(bool IsConnected, DateTime? LastMessageReceivedUtc, DateTime? ConnectAttemptedUtc);
