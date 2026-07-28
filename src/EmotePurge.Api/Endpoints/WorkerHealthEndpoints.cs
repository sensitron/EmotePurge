using System.Text.Json;
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
                return Results.Ok(new { status = "unknown", reason = "Kein aktueller Health-Status vom Worker (Key abgelaufen oder Worker nicht gestartet)." });
            }

            var payload = JsonSerializer.Deserialize<WorkerHealthPayload>((string)value!, JsonSerializerOptions.Web);
            if (payload is null)
            {
                return Results.Ok(new { status = "unknown", reason = "Health-Status konnte nicht gelesen werden." });
            }

            var secondsSinceLastMessage = payload.LastMessageReceivedUtc is { } lastMessage
                ? (int)(DateTime.UtcNow - lastMessage).TotalSeconds
                : (int?)null;

            return Results.Ok(new
            {
                status = payload.IsConnected ? "connected" : "disconnected",
                payload.IsConnected,
                payload.LastMessageReceivedUtc,
                secondsSinceLastMessage,
            });
        });
    }
}

internal sealed record WorkerHealthPayload(bool IsConnected, DateTime? LastMessageReceivedUtc);
