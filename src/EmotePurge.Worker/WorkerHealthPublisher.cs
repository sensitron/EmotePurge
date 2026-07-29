using System.Text.Json;
using StackExchange.Redis;

namespace EmotePurge.Worker;

// Schreibt periodisch den Twitch-Verbindungsstatus nach Redis (TTL-Key), damit die Api ihn
// über GET /api/worker/health nach außen zeigen kann, ohne dass Api und Worker direkt
// miteinander kommunizieren müssen — nutzt dieselbe Redis-Infrastruktur wie die
// channel:bot:commands-Pub/Sub. Läuft der Worker nicht (mehr), läuft der Key einfach ab.
public class WorkerHealthPublisher(
    ILogger<WorkerHealthPublisher> logger,
    ITwitchChatManager twitchChatManager,
    IConnectionMultiplexer redis) : BackgroundService
{
    public const string RedisKey = "worker:health:twitch";
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeyTtl = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PublishInterval);
        do
        {
            await PublishOnceAsync();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishOnceAsync()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                isConnected = twitchChatManager.IsConnected,
                lastMessageReceivedUtc = twitchChatManager.LastMessageReceivedUtc,
                // Reference point for the Api's staleness check while no chat message has ever
                // arrived — without it a freshly started worker is indistinguishable from one that
                // has been connected but silent for hours.
                connectAttemptedUtc = twitchChatManager.ConnectAttemptedUtc,
            });

            await redis.GetDatabase().StringSetAsync(RedisKey, payload, KeyTtl);
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagenes Health-Update darf den Worker-Host nicht mitreißen —
            // der Key läuft dann einfach ab und die Api zeigt korrekt "unbekannt/veraltet" an.
            logger.LogWarning(ex, "Health-Status-Publish nach Redis fehlgeschlagen.");
        }
    }
}
