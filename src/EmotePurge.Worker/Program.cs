using EmotePurge.Infrastructure;
using EmotePurge.Worker;
using EmotePurge.Worker.SevenTv;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
builder.Services.AddSingleton<IEmoteUsageCounter, EmoteUsageCounter>();
builder.Services.AddSingleton<BootRecoveryGate>();
builder.Services.AddSingleton<SevenTvSubscriptionRegistry>();
builder.Services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<UsageFlushWorker>();
builder.Services.AddHostedService<SevenTvPeriodicResyncWorker>();
builder.Services.AddHostedService<SevenTvEventWorker>();
builder.Services.AddHostedService<TwitchConnectionWatchdog>();
builder.Services.AddHostedService<WorkerHealthPublisher>();

var host = builder.Build();
host.Run();
