using System.Text.Json;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Worker.SevenTv;
using StackExchange.Redis;

namespace EmotePurge.Worker;

// Schreibt periodisch den Twitch-Verbindungsstatus nach Redis (TTL-Key), damit die Api ihn
// über GET /api/worker/health nach außen zeigen kann, ohne dass Api und Worker direkt
// miteinander kommunizieren müssen — nutzt dieselbe Redis-Infrastruktur wie die
// channel:bot:commands-Pub/Sub. Läuft der Worker nicht (mehr), läuft der Key einfach ab.
//
// Key, TTL und Payload-Typ kommen aus Infrastructure bzw. Core (WorkerHealthKeys,
// WorkerHealthSnapshot): Vorher war das Format hier ein anonymes Objekt und in der Api ein
// eigenes privates Record — dasselbe Wire-Format zweimal deklariert, ohne Verbindung.
public class WorkerHealthPublisher(
    ILogger<WorkerHealthPublisher> logger,
    ITwitchChatManager twitchChatManager,
    ISevenTvEventClient sevenTvEventClient,
    SevenTvSubscriptionRegistry subscriptionRegistry,
    WorkerStats stats,
    IEmoteUsageCounter usageCounter,
    IRedisPublisher redisPublisher,
    IConnectionMultiplexer redis) : BackgroundService
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(20);

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
            // Same key, one contract (see the class comment): the 7TV connection state rides in the
            // same snapshot instead of a second Redis key.
            var payload = JsonSerializer.Serialize(
                new WorkerHealthSnapshot(
                    twitchChatManager.IsConnected,
                    twitchChatManager.LastMessageReceivedUtc,
                    twitchChatManager.ConnectAttemptedUtc,
                    sevenTvEventClient.IsEnabled,
                    sevenTvEventClient.IsConnected,
                    sevenTvEventClient.LastFrameReceivedUtc,
                    sevenTvEventClient.LastDispatchReceivedUtc,
                    sevenTvEventClient.ConnectAttemptedUtc,
                    // Detail fields for the admin monitoring page — same key, same snapshot, no
                    // second Redis key (see the class comment).
                    subscriptionRegistry.DesiredChannels.Count,
                    subscriptionRegistry.DesiredSubscriptionCount,
                    subscriptionRegistry.UnacknowledgedCount,
                    stats.ConsecutiveFlushFailures,
                    stats.LastFlushSuccessUtc,
                    stats.LastFlushRowCount,
                    usageCounter.PendingEmoteCount),
                JsonSerializerOptions.Web);

            await redis.GetDatabase().StringSetAsync(WorkerHealthKeys.TwitchConnection, payload, WorkerHealthKeys.Ttl);

            // Nur die Benachrichtigung, nicht der Zustand: der Snapshot bleibt der TTL-Key oben,
            // das Event sagt bloß "es gibt einen neuen" — offene Admin-Seiten holen ihn sich dann
            // über GET /api/admin/health statt weiter im 20-Sekunden-Takt zu pollen. Bewusst im
            // bestehenden try: schlägt es fehl, gilt dieselbe Regel wie für das Health-Update.
            await redisPublisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.WorkerHealth).Serialize());
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagenes Health-Update darf den Worker-Host nicht mitreißen —
            // der Key läuft dann einfach ab und die Api zeigt korrekt "unbekannt/veraltet" an.
            logger.LogWarning(ex, "Health-Status-Publish nach Redis fehlgeschlagen.");
        }
    }
}
