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
// instead of the 503 LiveEndpoints.OpenAsync already renders for a failed subscription. See the twin
// fixes in RedisReaderFailureModeTests/ModRoleCacheFailureModeTests, whose fail-open shape this mirrors.
// Since #42, SubscribeAsync's failure carries a status rather than collapsing onto a bare null; the
// tests below now assert that status, and the last one pins InfrastructureUnavailable and
// QuotaExhausted apart — the very distinction #42 introduced.
public class RedisLiveEventStreamFailureModeTests
{
    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static RedisLiveEventStream CreateStream(IRedisSubscriber redisSubscriber, LiveEventStreamOptions? options = null) =>
        new(redisSubscriber, NullLogger<RedisLiveEventStream>.Instance, options);

    [Fact]
    public async Task SubscribeAsync_RedisSubscribeFails_ReturnsInfrastructureUnavailableInsteadOfThrowing()
    {
        var redisSubscriber = Substitute.For<IRedisSubscriber>();
        redisSubscriber
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw BuildConnectionException());

        var stream = CreateStream(redisSubscriber);

        var result = await stream.SubscribeAsync("user-1", _ => true);

        Assert.Equal(LiveEventSubscribeStatus.InfrastructureUnavailable, result.Status);
        Assert.Null(result.Subscription);
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
        Assert.Equal(LiveEventSubscribeStatus.InfrastructureUnavailable, first.Status);

        var second = await stream.SubscribeAsync("user-1", _ => true);
        Assert.Equal(LiveEventSubscribeStatus.Ok, second.Status);
        Assert.NotNull(second.Subscription);
        await second.Subscription!.DisposeAsync();

        await redisSubscriber.Received(2).SubscribeAsync(
            Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscribeAsync_QuotaExhausted_IsDistinctFromInfrastructureUnavailable()
    {
        // Redis works fine here — the rejection below comes purely from MaxPerSubscriber, never
        // touching EnsureRedisSubscribedAsync's failure path. Before #42 both reasons answered the
        // same null; this is the regression test for keeping them apart.
        var redisSubscriber = Substitute.For<IRedisSubscriber>();
        redisSubscriber
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var stream = CreateStream(redisSubscriber, new LiveEventStreamOptions { MaxPerSubscriber = 1 });

        var first = await stream.SubscribeAsync("user-2", _ => true);
        Assert.Equal(LiveEventSubscribeStatus.Ok, first.Status);

        var second = await stream.SubscribeAsync("user-2", _ => true);
        Assert.Equal(LiveEventSubscribeStatus.QuotaExhausted, second.Status);
        Assert.Null(second.Subscription);

        await first.Subscription!.DisposeAsync();
    }
}
