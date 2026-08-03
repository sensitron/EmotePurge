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
using Microsoft.Extensions.Logging;
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

        // Singleton by necessity, not convenience: it holds the one process-wide subscription to
        // live:events and the fan-out table of the open browser connections. Built through a factory
        // so its options record keeps its defaults instead of needing its own registration.
        services.AddSingleton<ILiveEventStream>(sp => new RedisLiveEventStream(
            sp.GetRequiredService<IRedisSubscriber>(),
            sp.GetRequiredService<ILogger<RedisLiveEventStream>>()));

        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<IAdminChannelQueryService, AdminChannelQueryService>();
        services.AddScoped<IAdminUserQueryService, AdminUserQueryService>();
        // Read side only — audit entries are written by the services that perform the actions, into
        // those actions' own transactions (see AuditLogWrites).
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<IEmoteService, EmoteService>();
        services.AddScoped<IEmoteSetOwnershipService, EmoteSetOwnershipService>();
        services.AddScoped<IEmoteSetStatusService, EmoteSetStatusService>();

        services.AddSingleton<IEmoteMatchCache, EmoteMatchCache>();
        services.AddSingleton<IDuplicateEmoteNameTracker, DuplicateEmoteNameTracker>();
        services.AddScoped<IUsageStatFlushService, UsageStatFlushService>();
        services.AddScoped<IUsageStatQueryService, UsageStatQueryService>();
        services.AddScoped<ILiveCoverageService, LiveCoverageService>();

        services.AddHttpClient<ISevenTvApiClient, SevenTvApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://7tv.io/v3/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmotePurge/1.0");
        });
        services.AddSingleton<ChannelSyncGate>();
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

        // Singleton cache over the transient typed client — see the class comment for why it
        // resolves ITwitchAuthClient through a scope instead of injecting it.
        services.AddSingleton<ITwitchAppTokenProvider, TwitchAppTokenProvider>();

        services.AddSingleton<ITokenCipher, AesGcmTokenCipher>();
        services.AddSingleton<TwitchTokenRefreshGate>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ITwitchUserTokenService, TwitchUserTokenService>();
        services.AddScoped<IModeratorCheckService, ModeratorCheckService>();
        services.AddScoped<ISevenTvEditorService, SevenTvEditorService>();
        services.AddScoped<IChannelAccessService, ChannelAccessService>();
        services.AddScoped<IMyChannelsService, MyChannelsService>();
        services.AddSingleton<IModRoleCache, ModRoleCache>();

        // Next to the role cache: same shape (a Redis key with a TTL), same lifetime, and both hold
        // no state of their own. Consumed only by the channel-scoped resync endpoint — the worker's
        // periodic resync must not see it.
        services.AddSingleton<IChannelResyncCooldown, ChannelResyncCooldown>();
        services.AddSingleton<IWorkerHealthReader, WorkerHealthReader>();
        services.AddSingleton<IWorkerRosterReader, WorkerRosterReader>();

        // One instance behind both interfaces: the worker writes, the API reads, the wire format
        // lives in one class (see TwitchLiveStatusStore).
        services.AddSingleton<TwitchLiveStatusStore>();
        services.AddSingleton<ITwitchLiveStatusReader>(sp => sp.GetRequiredService<TwitchLiveStatusStore>());
        services.AddSingleton<ITwitchLiveStatusWriter>(sp => sp.GetRequiredService<TwitchLiveStatusStore>());

        services.AddScoped<IVoteSessionService, VoteSessionService>();
        services.AddScoped<IVoteSessionQueryService, VoteSessionQueryService>();
        services.AddScoped<IVoteEligibilityService, VoteEligibilityService>();

        return services;
    }
}
