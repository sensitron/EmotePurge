using EmotePurge.Worker.SevenTv;

namespace EmotePurge.Worker;

// Drives the 7TV EventAPI client: one awaited session at a time, paced by SevenTvBackoffPolicy.
// Deliberately not fire-and-forget — the 2026-07 client started its connection loop with
// `_ = RunConnectionLoopAsync(...)`, so a task death was silent and unobservable. Here the loop
// is this service's ExecuteAsync, fully fault-isolated per iteration (the S2-5/S2-6 pattern);
// the server's ~1h connection TTL makes session ends the hourly normal case, not an error.
public class SevenTvEventWorker(
    ILogger<SevenTvEventWorker> logger,
    ISevenTvEventClient eventClient,
    BootRecoveryGate bootRecoveryGate) : BackgroundService
{
    private readonly SevenTvBackoffPolicy _backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!eventClient.IsEnabled)
        {
            logger.LogInformation(
                "7TV-EventAPI ist deaktiviert (SevenTv:EventApi:Enabled=false) — nur der periodische REST-Resync läuft.");
            return;
        }

        // Boot recovery fully syncs every channel first; subscriptions land in the registry
        // during that pass and the first session picks them up on its Hello.
        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            SevenTvSessionResult result;
            try
            {
                result = await eventClient.RunSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "7TV-EventAPI-Session unerwartet abgebrochen.");
                result = new SevenTvSessionResult(SessionEstablished: false, SevenTvSessionEndReason.Faulted);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var delay = _backoff.NextDelay(result);
            logger.LogInformation(
                "7TV-Session beendet ({Reason}), Reconnect in {Seconds}s.",
                result.Reason, (int)delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
