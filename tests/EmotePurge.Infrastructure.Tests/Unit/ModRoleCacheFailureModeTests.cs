using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free counterpart to Integration/ModRoleCacheTests.cs (real Redis, happy path). This suite
// substitutes IConnectionMultiplexer/IDatabase to reproduce what the user hit with Redis stopped: a
// RedisConnectionException surfacing from a "Try..." method all the way to the global exception
// handler. See issue #37 and the twin fix in ModeratedChannelsProvider.ReadCacheAsync/WriteCacheAsync,
// whose fail-open shape this mirrors.
public class ModRoleCacheFailureModeTests
{
    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = "10" })
            .Build();

    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static ModRoleCache CreateCacheWithFailingRedis()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>())
            .Returns<RedisValue>(_ => throw BuildConnectionException());
        // Matches the exact overload ModRoleCache's call resolves to: a TimeSpan argument implicitly
        // converts to Expiration, so `StringSetAsync(key, value, CacheTtl())` actually binds to
        // (RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags) — Arg.Any<TimeSpan>() would
        // leave a dangling, unmatched argument spec (RedundantArgumentMatcherException).
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>())
            .Returns<bool>(_ => throw BuildConnectionException());

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);

        return new ModRoleCache(connectionMultiplexer, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);
    }

    [Fact]
    public async Task TryGetSevenTvEditorGrantsAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var cache = CreateCacheWithFailingRedis();

        var result = await cache.TryGetSevenTvEditorGrantsAsync("user-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetSevenTvEditorGrantsAsync_RedisConnectionFails_DoesNotThrow()
    {
        var cache = CreateCacheWithFailingRedis();
        var grants = new SevenTvEditorGrants(new HashSet<string> { "channel-a" }, new HashSet<string> { "111" });

        // SetSevenTvEditorGrantsAsync sits one line after TryGetSevenTvEditorGrantsAsync on
        // SevenTvEditorService's success path (GetEditorGrantsAsync) — without this guard the request
        // would still die here, one line later than the read that already got fixed.
        var exception = await Record.ExceptionAsync(() => cache.SetSevenTvEditorGrantsAsync("user-1", grants));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryGetIsSubscriberAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var cache = CreateCacheWithFailingRedis();

        var result = await cache.TryGetIsSubscriberAsync("user-1", "broadcaster-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidateUserAsync_RedisConnectionFails_StillThrows()
    {
        // Deliberate asymmetry, not an oversight: InvalidateUserAsync's return value feeds an audit
        // entry (UserService -> AuditActions.UserInvalidateRoleCache). A silently swallowed failure
        // here would log "0 entries removed" for a role revocation that never happened — a false audit
        // record about a security-relevant action. This test pins the exception so nobody "fixes" the
        // asymmetry away later for consistency with the read/write paths above.
        var database = Substitute.For<IDatabase>();
        database.KeyDeleteAsync(Arg.Any<RedisKey[]>())
            .Returns<long>(_ => throw BuildConnectionException());

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);
        connectionMultiplexer.GetEndPoints().Returns(Array.Empty<EndPoint>());

        var cache = new ModRoleCache(connectionMultiplexer, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);

        await Assert.ThrowsAsync<RedisConnectionException>(() => cache.InvalidateUserAsync("user-1"));
    }
}
