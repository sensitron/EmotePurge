using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using EmotePurge.Worker;
using EmotePurge.Worker.SevenTv;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
builder.Services.AddSingleton<IEmoteUsageCounter, EmoteUsageCounter>();
builder.Services.AddSingleton<IBotChatterDetector, BotChatterDetector>();
builder.Services.AddSingleton<BootRecoveryGate>();
builder.Services.AddSingleton<WorkerStats>();
builder.Services.AddSingleton<WorkerIdentity>();
builder.Services.AddSingleton<SevenTvSubscriptionRegistry>();
builder.Services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
// Eight hosted services. The host starts them in registration order, and Worker deliberately goes
// first: it runs the boot recovery (rejoin every tracked channel, initial 7TV sync) that the
// others assume has happened. The ordering is not load-bearing on its own, though —
// BootRecoveryGate is what actually enforces it, because SevenTvPeriodicResyncWorker running
// concurrently with the boot sync for the same channel makes two AppDbContext instances insert
// the same (ChannelId, SevenTvEmoteId) rows and one loses on the unique index. Read that class
// before reordering anything here.
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<UsageFlushWorker>();
builder.Services.AddHostedService<SevenTvPeriodicResyncWorker>();
builder.Services.AddHostedService<SevenTvEventWorker>();
builder.Services.AddHostedService<TwitchConnectionWatchdog>();
builder.Services.AddHostedService<WorkerHealthPublisher>();
builder.Services.AddHostedService<WorkerRosterPublisher>();
builder.Services.AddHostedService<TwitchLivePollWorker>();

var host = builder.Build();

// S3-34 fail-fast, same guard as the Api: without it a worker against a stale schema boots,
// reports healthy, and fails only when the first flush or sync touches the missing column.
await using (var migrationScope = host.Services.CreateAsyncScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<IPendingMigrationGuard>()
        .EnsureNoPendingMigrationsAsync();
}

host.Run();
