using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class ModRoleCache(IConnectionMultiplexer connectionMultiplexer, IConfiguration configuration) : IModRoleCache
{
    public Task<bool?> TryGetIsModeratorAsync(string twitchUserId, string channelName, CancellationToken cancellationToken = default) =>
        TryGetAsync(BuildKey("modcheck", twitchUserId, channelName));

    public Task SetIsModeratorAsync(string twitchUserId, string channelName, bool isModerator, CancellationToken cancellationToken = default) =>
        SetAsync(BuildKey("modcheck", twitchUserId, channelName), isModerator);

    public Task<bool?> TryGetIsSevenTvEditorAsync(string twitchUserId, string channelName, CancellationToken cancellationToken = default) =>
        TryGetAsync(BuildKey("7tveditor", twitchUserId, channelName));

    public Task SetIsSevenTvEditorAsync(string twitchUserId, string channelName, bool isEditor, CancellationToken cancellationToken = default) =>
        SetAsync(BuildKey("7tveditor", twitchUserId, channelName), isEditor);

    public Task<bool?> TryGetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, CancellationToken cancellationToken = default) =>
        TryGetAsync(BuildKey("subcheck", twitchUserId, broadcasterTwitchId));

    public Task SetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, bool isSubscriber, CancellationToken cancellationToken = default) =>
        SetAsync(BuildKey("subcheck", twitchUserId, broadcasterTwitchId), isSubscriber);

    private async Task<bool?> TryGetAsync(string key)
    {
        var value = await connectionMultiplexer.GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : value == "1";
    }

    private async Task SetAsync(string key, bool value)
    {
        var ttlMinutes = configuration.GetValue<int?>("Auth:ModCheckCacheTtlMinutes") ?? 10;
        await connectionMultiplexer.GetDatabase().StringSetAsync(key, value ? "1" : "0", TimeSpan.FromMinutes(ttlMinutes));
    }

    // The first segment after the prefix is always the numeric Twitch user id, so one user's entries
    // can never collide with another's regardless of what the second segment contains.
    private static string BuildKey(string prefix, string twitchUserId, string scope) => $"{prefix}:{twitchUserId}:{scope}";
}
