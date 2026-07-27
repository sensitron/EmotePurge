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
}
