using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Runs against a real redis:7.2-alpine container — verifies the TTL cache's actual
// StackExchange.Redis wiring (key format, string-encoded bool, TTL), not just the interface
// contract mocks would give us.
[Collection("Redis")]
public class ModRoleCacheTests(RedisFixture fixture)
{
    private static IConfiguration BuildConfiguration(int ttlMinutes = 10) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = ttlMinutes.ToString() })
            .Build();

    [Fact]
    public async Task TryGetIsModeratorAsync_ReturnsNull_ForCacheMiss()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());

        var result = await cache.TryGetIsModeratorAsync("user-never-cached", "somechannel");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetIsModeratorAsync_ThenTryGet_RoundTripsTrue()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());

        await cache.SetIsModeratorAsync("user-mod", "channel-a", isModerator: true);
        var result = await cache.TryGetIsModeratorAsync("user-mod", "channel-a");

        Assert.True(result);
    }

    [Fact]
    public async Task SetIsModeratorAsync_ThenTryGet_RoundTripsFalse()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());

        await cache.SetIsModeratorAsync("user-notmod", "channel-b", isModerator: false);
        var result = await cache.TryGetIsModeratorAsync("user-notmod", "channel-b");

        Assert.False(result);
    }

    [Fact]
    public async Task Cache_IsScoped_PerChannel_NotJustPerUser()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());

        await cache.SetIsModeratorAsync("user-multi", "channel-x", isModerator: true);

        Assert.True(await cache.TryGetIsModeratorAsync("user-multi", "channel-x"));
        Assert.Null(await cache.TryGetIsModeratorAsync("user-multi", "channel-y"));
    }

    [Fact]
    public async Task InvalidateUserAsync_RemovesModSubscriberAndEditorEntries_AcrossAllChannels()
    {
        // The admin escape hatch has to reach every key shape at once — a per-channel modcheck
        // survivor would leave exactly the stale answer the invalidation was triggered for.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());
        await cache.SetIsModeratorAsync("user-invalidate-1", "channel-a", isModerator: true);
        await cache.SetIsModeratorAsync("user-invalidate-1", "channel-b", isModerator: false);
        await cache.SetIsSubscriberAsync("user-invalidate-1", "broadcaster-1", isSubscriber: true);
        await cache.SetSevenTvEditorGrantsAsync(
            "user-invalidate-1",
            new SevenTvEditorGrants(new HashSet<string> { "channel-a" }, new HashSet<string> { "broadcaster-1" }));

        var removed = await cache.InvalidateUserAsync("user-invalidate-1");

        Assert.Equal(4, removed);
        Assert.Null(await cache.TryGetIsModeratorAsync("user-invalidate-1", "channel-a"));
        Assert.Null(await cache.TryGetIsModeratorAsync("user-invalidate-1", "channel-b"));
        Assert.Null(await cache.TryGetIsSubscriberAsync("user-invalidate-1", "broadcaster-1"));
        Assert.Null(await cache.TryGetSevenTvEditorGrantsAsync("user-invalidate-1"));
    }

    [Fact]
    public async Task InvalidateUserAsync_LeavesOtherUsersEntriesUntouched()
    {
        // The SCAN pattern is built from the user id, so a too-greedy glob would silently flush
        // every logged-in user's role answers instead of one person's.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());
        await cache.SetIsModeratorAsync("user-invalidate-2", "channel-a", isModerator: true);
        await cache.SetIsModeratorAsync("user-invalidate-3", "channel-a", isModerator: true);
        await cache.SetIsSubscriberAsync("user-invalidate-3", "broadcaster-1", isSubscriber: true);

        var removed = await cache.InvalidateUserAsync("user-invalidate-2");

        Assert.Equal(1, removed);
        Assert.True(await cache.TryGetIsModeratorAsync("user-invalidate-3", "channel-a"));
        Assert.True(await cache.TryGetIsSubscriberAsync("user-invalidate-3", "broadcaster-1"));
    }

    [Fact]
    public async Task InvalidateUserAsync_ForUserWithoutEntries_ReturnsZero()
    {
        // Including the unconditionally probed 7tveditor key: it is only counted when it existed.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration());

        Assert.Equal(0, await cache.InvalidateUserAsync("user-invalidate-nobody"));
    }
}
