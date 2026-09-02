using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

// Keeps stored channel rows in step with Twitch's own view of who they are (issue #44): the login
// is a display name an owner may rename at any time without Twitch telling anyone, so the only way
// to notice is to periodically ask Helix. Waits for BootRecoveryGate on purpose — unlike
// TwitchLivePollWorker, which only ever writes coverage rows nothing else touches, this worker can
// rename or merge the very channel rows boot recovery itself reads and writes, so the two must not
// overlap.
public class TwitchIdentityReconcileWorker(
    ILogger<TwitchIdentityReconcileWorker> logger,
    BootRecoveryGate bootRecoveryGate,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly ChannelIdentityReconcileSummary EmptySummary = new(0, 0, 0, 0, 0, 0);

    // 60 minutes default (Betreiber-Antwort 2): one tick costs one Helix request per 100 channels,
    // and renames are rare enough that hourly is plenty responsive.
    private readonly TimeSpan _reconcileInterval =
        TimeSpan.FromMinutes(configuration.GetValue("Twitch:IdentityReconcileIntervalMinutes", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without client credentials every tick would fail on the token fetch — say it once
        // instead of warning forever. The Api shares the same config values, so a correctly
        // configured deployment cannot land here.
        if (string.IsNullOrEmpty(configuration["Auth:Twitch:ClientId"]) ||
            string.IsNullOrEmpty(configuration["Auth:Twitch:ClientSecret"]))
        {
            logger.LogWarning("Twitch-Identity-Reconcile deaktiviert — Auth:Twitch:ClientId/ClientSecret nicht konfiguriert.");
            return;
        }

        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);

        // First run happens immediately, before the timer: this is the production backfill for
        // channel rows that predate the immutable-id migration (Entscheidung 6), not just the
        // first regular tick.
        await ReconcileOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_reconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileOnceAsync(stoppingToken);
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var identityService = scope.ServiceProvider.GetRequiredService<IChannelIdentityService>();
            var summary = await identityService.ReconcileActiveChannelsAsync(ct);

            // null means the tick was skipped (no app token, Helix unreachable) — deliberately
            // not the empty summary, and not worth a log line either: the next tick retries.
            if (summary is null || summary == EmptySummary)
            {
                return;
            }

            logger.LogInformation(
                "Identity-Reconcile: {Checked} geprüft, {IdsBackfilled} IDs nachgetragen, {Renamed} umbenannt, {Merged} zusammengeführt, {MergesRefused} Zusammenführungen abgelehnt, {LoginsMissing} Logins nicht mehr bei Twitch bekannt.",
                summary.Checked, summary.IdsBackfilled, summary.Renamed, summary.Merged, summary.MergesRefused, summary.LoginsMissing);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception ex)
        {
            // Runs forever on a timer — a Postgres hiccup or a Twitch outage must cost one tick,
            // never the worker host (StopHost default would take the usage flush down with it).
            logger.LogWarning(ex, "Identity-Reconcile-Durchlauf fehlgeschlagen.");
        }
    }
}
