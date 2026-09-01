using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public class UsageFlushWorker(
    ILogger<UsageFlushWorker> logger,
    IEmoteUsageCounter usageCounter,
    WorkerStats stats,
    IRedisPublisher redisPublisher,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    // How often in a row a failed batch is put back before it is dropped. The bound is not about
    // memory (the counter is bounded by the number of distinct emotes) but about attribution:
    // UsageStat.Date is the day the flush *succeeds*, so counts carried across a long outage would
    // eventually be booked on the wrong calendar day. Five attempts ≈ 2.5 minutes of tolerance.
    private const int MaxConsecutiveFailuresToRequeue = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await FlushOnceAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // base.StopAsync first: it cancels the stopping token and thereby ends the PeriodicTimer
        // loop. Flushing before that left the loop armed, so a due 30s tick could run concurrently
        // with the final flush — two writers on the same (EmoteId, Date) rows.
        await base.StopAsync(cancellationToken);

        // Final flush so the last <30s of buffered counts aren't lost on a normal shutdown.
        await FlushOnceAsync(cancellationToken);
    }

    private async Task FlushOnceAsync(CancellationToken ct)
    {
        var counts = usageCounter.DrainAndReset();
        if (counts.Count == 0)
        {
            return;
        }

        IReadOnlyCollection<string> affectedChannels;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var flushService = scope.ServiceProvider.GetRequiredService<IUsageStatFlushService>();
            affectedChannels = await flushService.FlushAsync(counts, ct);
            // Bookkeeping moved to the shared WorkerStats so GET /api/admin/health can report it;
            // the requeue/drop behaviour below is unchanged.
            stats.RecordFlushSuccess(counts.Count, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // An unhandled exception here would (StopHost default) kill the whole Worker
            // process, not just this flush cycle — so it never propagates.
            var consecutiveFailures = stats.RecordFlushFailure();
            if (consecutiveFailures <= MaxConsecutiveFailuresToRequeue)
            {
                usageCounter.Merge(counts);
                logger.LogWarning(
                    ex,
                    "Usage-Stat-Flush fehlgeschlagen ({Attempt}. Versuch in Folge), {Count} Counts für den nächsten Durchlauf zurückgestellt.",
                    consecutiveFailures, counts.Count);
            }
            else
            {
                logger.LogError(
                    ex,
                    "Usage-Stat-Flush seit {Attempt} Durchläufen fehlgeschlagen, {Count} Counts verworfen.",
                    consecutiveFailures, counts.Count);
            }

            return;
        }

        // Deliberately its own try/catch, and deliberately *after* RecordFlushSuccess: at this point
        // the flush is committed. If a Redis outage were allowed to fall into the catch above, a
        // successful flush would be booked as a failure and its counts requeued — the next flush
        // would then add them a second time onto the same (EmoteId, Date) row (ON CONFLICT DO UPDATE
        // ... + EXCLUDED, now for both UseCount and BotUseCount), i.e. silently double-count chat
        // usage. A missed notification only costs the browser its automatic refresh; every page
        // still has its refresh button.
        try
        {
            foreach (var channelName in affectedChannels)
            {
                await redisPublisher.PublishAsync(
                    LiveEvents.Channel,
                    new LiveEvent(LiveEvents.UsageFlushed, channelName).Serialize(),
                    ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live-Event '{Type}' konnte nicht veröffentlicht werden.", LiveEvents.UsageFlushed);
        }
    }
}
