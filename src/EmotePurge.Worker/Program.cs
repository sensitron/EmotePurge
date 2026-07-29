using EmotePurge.Infrastructure;
using EmotePurge.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
builder.Services.AddSingleton<IEmoteUsageCounter, EmoteUsageCounter>();
builder.Services.AddSingleton<BootRecoveryGate>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<UsageFlushWorker>();
builder.Services.AddHostedService<SevenTvPeriodicResyncWorker>();
builder.Services.AddHostedService<TwitchConnectionWatchdog>();
builder.Services.AddHostedService<WorkerHealthPublisher>();

var host = builder.Build();
host.Run();
