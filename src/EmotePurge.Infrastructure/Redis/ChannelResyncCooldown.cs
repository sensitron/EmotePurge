using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IChannelResyncCooldown"/>. Redis rather than an
/// in-process guard or a database column: the API is expected to run with more than one replica,
/// and a per-process cooldown would multiply with the replica count. A column would additionally
/// need a migration, and the obvious candidate to reuse — <c>Channel.LastSyncedAtUtc</c> — is
/// written by the worker's own 60-second resync and is therefore never stale enough to guard on.
/// </summary>
public class ChannelResyncCooldown(
    IConnectionMultiplexer connectionMultiplexer,
    IConfiguration configuration,
    ILogger<ChannelResyncCooldown> logger) : IChannelResyncCooldown
{
    private const int DefaultCooldownSeconds = 60;

    public async Task<ResyncCooldownState> TryBeginAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var db = connectionMultiplexer.GetDatabase();
        var key = BuildKey(channelName);
        var cooldown = Cooldown();

        try
        {
            // SET key value EX <ttl> NX — the first and only use of When.NotExists in this codebase.
            // The TTL is not optional decoration: without it a crashed request would leave a key
            // behind that blocks the channel's resyncs forever, with no code path anywhere that
            // deletes it.
            var acquired = await db.StringSetAsync(key, "1", cooldown, When.NotExists);
            if (acquired)
            {
                return new ResyncCooldownState(true, 0);
            }

            // Re-read rather than reporting the configured length: the caller wants to know how much
            // is left, not how long the window is. The fallback covers the real race where the key
            // expires between the SET above and this read.
            var remaining = await db.KeyTimeToLiveAsync(key);
            return new ResyncCooldownState(false, ToRetryAfterSeconds(remaining));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail-open, not fail-closed (issue #41, see docs/DECISIONS.md): this cooldown is only
            // half a guard against a resync storm — POST /{channelName}/resync sits behind its own
            // per-user fixed-window limiter (RateLimitPolicyNames.ChannelResync) that is entirely
            // in-process and unaffected by a Redis outage, so an outage only drops the per-*channel*
            // half (many users of the same channel sharing one budget), not abuse protection
            // altogether. A 503 here would instead make self-service resync entirely unavailable for
            // the whole outage, for a guard that is UX cost-control, not a security boundary.
            logger.LogWarning(ex, "Resync-Cooldown für {Channel} nicht erreichbar — lasse den Resync ohne Cooldown zu.", channelName);
            return new ResyncCooldownState(true, 0);
        }
    }

    public async Task ReleaseAsync(string channelName, CancellationToken cancellationToken = default)
    {
        try
        {
            await connectionMultiplexer.GetDatabase().KeyDeleteAsync(BuildKey(channelName));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Same fail-open direction as TryBeginAsync. A lost release only costs the caller a
            // cooldown window it did not need to serve (the channel was not found or not active), not
            // a correctness problem — nothing else reads this key.
            logger.LogWarning(ex, "Freigeben des Resync-Cooldowns für {Channel} fehlgeschlagen — ignoriert.", channelName);
        }
    }

    private TimeSpan Cooldown() =>
        TimeSpan.FromSeconds(configuration.GetValue<int?>("SevenTv:ManualResyncCooldownSeconds") ?? DefaultCooldownSeconds);

    // Normalized here rather than at the call site (Regel 9): "HandOfBlood" and "handofblood" are one
    // channel, and a cooldown that could be bypassed by changing the capitalization would be none.
    private static string BuildKey(string channelName) => $"resync:cooldown:{ChannelName.Normalize(channelName)}";

    private static int ToRetryAfterSeconds(TimeSpan? remaining) =>
        Math.Max(1, (int)Math.Ceiling(remaining?.TotalSeconds ?? 1));
}
