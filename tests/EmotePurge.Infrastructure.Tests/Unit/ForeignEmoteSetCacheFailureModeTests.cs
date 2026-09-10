using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free counterpart to Integration/ForeignEmoteSetCacheTests.cs (real Redis, corrupt
// payload). Reproduces the other half of the class remark on ForeignEmoteSetCache — a Redis outage,
// not a bad payload — the same shape as ModRoleCacheFailureModeTests/
// ChannelResyncCooldownFailureModeTests: substitute IConnectionMultiplexer/IDatabase so
// StringGetAsync/StringSetAsync throw a RedisConnectionException, and pin that TryGetAsync degrades
// to a miss while SetAsync swallows the failure outright, matching "a Redis outage degrades this
// feature to 'always live', not to a 503".
public class ForeignEmoteSetCacheFailureModeTests
{
    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static ForeignEmoteSetCache CreateCacheWithFailingRedis()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>())
            .Returns<RedisValue>(_ => throw BuildConnectionException());
        // ForeignEmoteSetCache.SetAsync calls StringSetAsync(key, value, Ttl) with only three
        // positional args; the bare TimeSpan in third position resolves to the (RedisKey, RedisValue,
        // Expiration, ValueCondition, CommandFlags) overload via TimeSpan's implicit conversion to
        // Expiration — the same overload-resolution trap ModRoleCacheFailureModeTests already
        // documents. Arg.Any<TimeSpan>() would leave this unmatched and fall through to a real
        // (non-throwing) substitute default instead.
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>())
            .Returns<bool>(_ => throw BuildConnectionException());

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);

        return new ForeignEmoteSetCache(connectionMultiplexer, NullLogger<ForeignEmoteSetCache>.Instance);
    }

    [Fact]
    public async Task TryGetAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var cache = CreateCacheWithFailingRedis();

        var result = await cache.TryGetAsync("handofblood");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_RedisConnectionFails_DoesNotThrow()
    {
        var cache = CreateCacheWithFailingRedis();
        var emoteSet = new ForeignEmoteSet("handofblood", "01FRY81K4800085N93FNKSBYXS", "01FRY81K4800085N93FNKSBYXS-set", 1, false, []);

        // SetAsync sits right after a successful lookup on HardenedForeignEmoteSetService's cache-miss
        // path — without this guard the request would still die here, just one step later than the
        // read that TryGetAsync's twin test above already covers.
        var exception = await Record.ExceptionAsync(() => cache.SetAsync("handofblood", emoteSet));

        Assert.Null(exception);
    }
}
