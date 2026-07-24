using EmotePurge.Infrastructure;
using EmotePurge.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);
builder.Services.AddSingleton<TwitchChatManager>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
