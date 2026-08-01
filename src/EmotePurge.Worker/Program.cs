using EmotePurge.Infrastructure;
using EmotePurge.Worker;
using EmotePurge.Worker.SevenTv;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
builder.Services.AddSingleton<IEmoteUsageCounter, EmoteUsageCounter>();
builder.Services.AddSingleton<BootRecoveryGate>();
builder.Services.AddSingleton<WorkerStats>();
builder.Services.AddSingleton<SevenTvSubscriptionRegistry>();
builder.Services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
// Six hosted services. The host starts them in registration order, and Worker deliberately goes
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

var host = builder.Build();
host.Run();
