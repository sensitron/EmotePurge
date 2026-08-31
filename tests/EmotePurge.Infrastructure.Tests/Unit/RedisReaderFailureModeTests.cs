using System.Net;
using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free counterpart to the Integration/*Tests.cs happy-path suites for these two readers.
// Reproduces what issue #37 measured with Redis stopped: StringGetAsync throwing a
// RedisConnectionException all the way out of ReadAsync, past the API's ExceptionHandlerMiddleware.
// Both readers already return null for a missing key, and every consumer (MyChannelsService,
// /api/worker/health, /api/health) already treats that null as "no data right now" — this guard just
// makes an unreachable Redis collapse into the same null instead of throwing. See the twin fix in
// ModRoleCacheFailureModeTests, whose fail-open shape this mirrors.
public class RedisReaderFailureModeTests
{
    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static IConnectionMultiplexer BuildFailingConnectionMultiplexer()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>())
            .Returns<RedisValue>(_ => throw BuildConnectionException());

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);
        return connectionMultiplexer;
    }

    [Fact]
    public async Task TwitchLiveStatusStore_ReadAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var store = new TwitchLiveStatusStore(BuildFailingConnectionMultiplexer(), NullLogger<TwitchLiveStatusStore>.Instance);

        var result = await store.ReadAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task WorkerHealthReader_ReadAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var reader = new WorkerHealthReader(BuildFailingConnectionMultiplexer(), NullLogger<WorkerHealthReader>.Instance);

        var result = await reader.ReadAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task WorkerRosterReader_ReadAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var reader = new WorkerRosterReader(BuildFailingConnectionMultiplexer(), NullLogger<WorkerRosterReader>.Instance);

        var result = await reader.ReadAsync();

        Assert.Null(result);
    }
}
