using EmotePurge.Infrastructure;
using EmotePurge.Worker;
using EmotePurge.Worker.SevenTv;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
builder.Services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
