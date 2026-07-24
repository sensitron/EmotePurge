using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public class UsageFlushWorker(
    ILogger<UsageFlushWorker> logger,
    IEmoteUsageCounter usageCounter,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

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
        // Final flush so the last <30s of buffered counts aren't lost on a normal shutdown.
        await FlushOnceAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
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
        }
        catch (Exception ex)
        {
            // An unhandled exception here would (StopHost default) kill the whole Worker
            // process, not just this flush cycle — log-and-drop is the safer trade-off
            // for a best-effort analytics counter.
            logger.LogWarning(ex, "Usage-Stat-Flush fehlgeschlagen, {Count} Counts verworfen.", counts.Count);
        }
    }
}
