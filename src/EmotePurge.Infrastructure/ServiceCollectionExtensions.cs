using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Twitch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmotePurgeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        var redisConnectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Konfigurationswert 'Redis:ConnectionString' fehlt.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));

        services.AddSingleton<IRedisPublisher, RedisPublisher>();
        services.AddSingleton<IRedisSubscriber, RedisSubscriber>();

        services.AddScoped<IChannelService, ChannelService>();

        services.AddSingleton<IEmoteMatchCache, EmoteMatchCache>();
        services.AddScoped<IUsageStatFlushService, UsageStatFlushService>();
        services.AddScoped<IUsageStatQueryService, UsageStatQueryService>();

        services.AddHttpClient<ISevenTvApiClient, SevenTvApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://7tv.io/v3/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmotePurge/1.0");
        });
        services.AddScoped<ISevenTvSyncService, SevenTvSyncService>();

        services.AddHttpClient<ITwitchAuthClient, TwitchAuthClient>(client =>
        {
            client.BaseAddress = new Uri("https://id.twitch.tv/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        var twitchClientId = configuration["Auth:Twitch:ClientId"];
        services.AddHttpClient<ITwitchHelixClient, TwitchHelixClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.twitch.tv/helix/");
            client.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrEmpty(twitchClientId))
            {
                client.DefaultRequestHeaders.Add("Client-Id", twitchClientId);
            }
        });

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IChannelAccessService, ChannelAccessService>();
        services.AddSingleton<IModRoleCache, ModRoleCache>();

        return services;
    }
}
