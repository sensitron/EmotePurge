using System.Net;
using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free counterpart to Integration/RedisLiveEventStreamTests.cs (real Redis, happy path).
// Reproduces what issue #37 measured with Redis stopped: GET /api/channels/live-events answering 500
// instead of the 503 LiveEndpoints.OpenAsync already renders for a null subscription. See the twin
// fixes in RedisReaderFailureModeTests/ModRoleCacheFailureModeTests, whose fail-open shape this mirrors.
public class RedisLiveEventStreamFailureModeTests
{
    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static RedisLiveEventStream CreateStream(IRedisSubscriber redisSubscriber) =>
        new(redisSubscriber, NullLogger<RedisLiveEventStream>.Instance);

    [Fact]
    public async Task SubscribeAsync_RedisSubscribeFails_ReturnsNullInsteadOfThrowing()
    {
        var redisSubscriber = Substitute.For<IRedisSubscriber>();
        redisSubscriber
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw BuildConnectionException());

        var stream = CreateStream(redisSubscriber);

        var subscription = await stream.SubscribeAsync("user-1", _ => true);

        Assert.Null(subscription);
    }

    [Fact]
    public async Task SubscribeAsync_AfterFailure_RetriesRealSubscribeInsteadOfStayingBurned()
    {
        // The flag EnsureRedisSubscribedAsync guards ("already subscribed to the Redis channel") must
        // not be set on a failed attempt — otherwise a transient outage would kill the live-event
        // stream for the rest of the process's lifetime instead of just for the requests made while
        // Redis was actually down.
        var callCount = 0;
        var redisSubscriber = Substitute.For<IRedisSubscriber>();
        redisSubscriber
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? throw BuildConnectionException() : Task.CompletedTask;
            });

        var stream = CreateStream(redisSubscriber);

        var first = await stream.SubscribeAsync("user-1", _ => true);
        Assert.Null(first);

        var second = await stream.SubscribeAsync("user-1", _ => true);
        Assert.NotNull(second);
        await second.DisposeAsync();

        await redisSubscriber.Received(2).SubscribeAsync(
            Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>());
    }
}
