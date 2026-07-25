using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class ModRoleCache(IConnectionMultiplexer connectionMultiplexer, IConfiguration configuration) : IModRoleCache
{
    public async Task<bool?> TryGetIsModeratorAsync(string twitchUserId, string channelName, CancellationToken cancellationToken = default)
    {
        var db = connectionMultiplexer.GetDatabase();
        var value = await db.StringGetAsync(BuildKey(twitchUserId, channelName));
        return value.IsNullOrEmpty ? null : value == "1";
    }

    public async Task SetIsModeratorAsync(string twitchUserId, string channelName, bool isModerator, CancellationToken cancellationToken = default)
    {
        var ttlMinutes = configuration.GetValue<int?>("Auth:ModCheckCacheTtlMinutes") ?? 10;
        var db = connectionMultiplexer.GetDatabase();
        await db.StringSetAsync(BuildKey(twitchUserId, channelName), isModerator ? "1" : "0", TimeSpan.FromMinutes(ttlMinutes));
    }

    private static string BuildKey(string twitchUserId, string channelName) => $"modcheck:{twitchUserId}:{channelName}";
}
