using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class ModRoleCache(IConnectionMultiplexer connectionMultiplexer, IConfiguration configuration) : IModRoleCache
{
    public async Task<SevenTvEditorGrants?> TryGetSevenTvEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var value = await connectionMultiplexer.GetDatabase().StringGetAsync($"7tveditor:{twitchUserId}");
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        // A payload we cannot read is treated as a miss rather than as "no grants" — the caller then
        // resolves live, which is the safe direction for an authorization input.
        var stored = JsonSerializer.Deserialize<StoredEditorGrants>((string)value!, JsonSerializerOptions.Web);
        if (stored is null)
        {
            return null;
        }

        // A pre-upgrade payload has no "entries" property at all, so the constructor's default
        // applies and Entries comes back null here — never an exception, never read as "no grants".
        // That maps to an empty list, which combined with a non-empty ChannelLogins is exactly the
        // legacy signal MyChannelsService looks for.
        return new SevenTvEditorGrants(ToSet(stored.ChannelLogins), ToSet(stored.TwitchChannelIds), stored.Entries ?? []);
    }

    public async Task SetSevenTvEditorGrantsAsync(string twitchUserId, SevenTvEditorGrants grants, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new StoredEditorGrants([.. grants.ChannelLogins], [.. grants.TwitchChannelIds], [.. grants.Entries]),
            JsonSerializerOptions.Web);
        await connectionMultiplexer.GetDatabase().StringSetAsync($"7tveditor:{twitchUserId}", payload, CacheTtl());
    }

    public Task<bool?> TryGetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, CancellationToken cancellationToken = default) =>
        TryGetAsync(BuildKey("subcheck", twitchUserId, broadcasterTwitchId));

    public Task SetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, bool isSubscriber, CancellationToken cancellationToken = default) =>
        SetAsync(BuildKey("subcheck", twitchUserId, broadcasterTwitchId), isSubscriber);

    public async Task<int> InvalidateUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        // Both directly addressable keys are named from the user id alone; only the per-broadcaster
        // subcheck keys need a SCAN — acceptable here: the keyspace is small (role checks for
        // logged-in users only) and the call is a rare, admin-triggered action, not a request path.
        // KeysAsync uses cursor-based SCAN under the hood, never the blocking KEYS command.
        //
        // No modcheck:* SCAN any more: nothing writes those keys since the moderated-channel list
        // replaced the per-channel bool. Entries left over from a previous deployment are not worth
        // scanning for — they expire on their own within the TTL (ten minutes by default) and no
        // reader ever looks at them again.
        // The modlist key format has to stay in step with ModeratedChannelsProvider.BuildKey — this
        // is the only place outside that service that names it.
        var keys = new List<RedisKey> { $"7tveditor:{twitchUserId}", $"modlist:{twitchUserId}" };
        foreach (var endpoint in connectionMultiplexer.GetEndPoints())
        {
            var server = connectionMultiplexer.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: $"subcheck:{twitchUserId}:*").WithCancellation(cancellationToken))
            {
                keys.Add(key);
            }
        }

        // KeyDelete reports how many keys existed — the two guessed keys above are only counted
        // when they actually held something.
        return (int)await connectionMultiplexer.GetDatabase().KeyDeleteAsync([.. keys.Distinct()]);
    }

    private async Task<bool?> TryGetAsync(string key)
    {
        var value = await connectionMultiplexer.GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : value == "1";
    }

    private async Task SetAsync(string key, bool value)
    {
        await connectionMultiplexer.GetDatabase().StringSetAsync(key, value ? "1" : "0", CacheTtl());
    }

    private TimeSpan CacheTtl() => TimeSpan.FromMinutes(configuration.GetValue<int?>("Auth:ModCheckCacheTtlMinutes") ?? 10);

    // Normalized on write, but compared case-insensitively anyway: a caller that forgets to normalize
    // should get a wrong-cased hit rather than a silent miss that reads as "not an editor".
    private static IReadOnlySet<string> ToSet(IReadOnlyList<string> values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    // The first segment after the prefix is always the numeric Twitch user id, so one user's entries
    // can never collide with another's regardless of what the second segment contains.
    private static string BuildKey(string prefix, string twitchUserId, string scope) => $"{prefix}:{twitchUserId}:{scope}";

    // Entries defaults to null (not []) so a missing "entries" property in the JSON — the shape a
    // pre-upgrade cache entry has — is distinguishable from a current write that legitimately found
    // zero grants: the caller of TryGetSevenTvEditorGrantsAsync maps null to [] itself.
    private sealed record StoredEditorGrants(IReadOnlyList<string> ChannelLogins, IReadOnlyList<string> TwitchChannelIds, IReadOnlyList<SevenTvEditorGrantEntry>? Entries = null);
}
