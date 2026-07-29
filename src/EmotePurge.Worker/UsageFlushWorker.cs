using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public class UsageFlushWorker(
    ILogger<UsageFlushWorker> logger,
    IEmoteUsageCounter usageCounter,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    // How often in a row a failed batch is put back before it is dropped. The bound is not about
    // memory (the counter is bounded by the number of distinct emotes) but about attribution:
    // UsageStat.Date is the day the flush *succeeds*, so counts carried across a long outage would
    // eventually be booked on the wrong calendar day. Five attempts ≈ 2.5 minutes of tolerance.
    private const int MaxConsecutiveFailuresToRequeue = 5;

    private int _consecutiveFailures;

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

        try
        {
            using var scope = scopeFactory.CreateScope();
            var flushService = scope.ServiceProvider.GetRequiredService<IUsageStatFlushService>();
            await flushService.FlushAsync(counts, ct);
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            // An unhandled exception here would (StopHost default) kill the whole Worker
            // process, not just this flush cycle — so it never propagates.
            _consecutiveFailures++;
            if (_consecutiveFailures <= MaxConsecutiveFailuresToRequeue)
            {
                usageCounter.Merge(counts);
                logger.LogWarning(
                    ex,
                    "Usage-Stat-Flush fehlgeschlagen ({Attempt}. Versuch in Folge), {Count} Counts für den nächsten Durchlauf zurückgestellt.",
                    _consecutiveFailures, counts.Count);
            }
            else
            {
                logger.LogError(
                    ex,
                    "Usage-Stat-Flush seit {Attempt} Durchläufen fehlgeschlagen, {Count} Counts verworfen.",
                    _consecutiveFailures, counts.Count);
            }
        }
    }
}
