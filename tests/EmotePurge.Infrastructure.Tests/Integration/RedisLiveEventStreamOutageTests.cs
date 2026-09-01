using System.Net;
using System.Net.Sockets;
using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// What a Redis outage does to an already-running live-event fan-out. Deliberately outside the
/// shared "Redis" collection and on its own container: the scenario has to stop and restart Redis,
/// which every other test in that collection would notice.
/// <para>
/// This pins the behaviour that argues against "fixing" the <c>_redisSubscribed</c> latch in
/// <see cref="RedisLiveEventStream"/> — see the comment on <c>EnsureRedisSubscribedAsync</c>. The
/// load-bearing fact is a third-party one (StackExchange.Redis restoring its subscriptions after a
/// reconnect), so it is measured against a real container that really goes away; a fake
/// <see cref="IRedisSubscriber"/> would assert our assumption about the library rather than the
/// library's behaviour.
/// </para>
/// </summary>
public class RedisLiveEventStreamOutageTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RedisOutage_DeniesAColdStream_ButOpenConnectionsHealThemselvesOnReconnect()
    {
        // A fixed host port, not Testcontainers' random mapping: the container is stopped and started
        // again, and only a pinned port keeps the address the multiplexer reconnects to valid —
        // which is what `docker compose stop redis` looks like from inside the Api.
        var container = new RedisBuilder()
            .WithImage("redis:7.2-alpine")
            .WithPortBinding(FreeTcpPort(), 6379)
            .Build();

        await container.StartAsync();
        try
        {
            var config = ConfigurationOptions.Parse(container.GetConnectionString());
            // Without this the multiplexer would give up instead of reconnecting, and the test would
            // measure ConnectionMultiplexer configuration rather than outage behaviour.
            config.AbortOnConnectFail = false;
            await using var connection = await ConnectionMultiplexer.ConnectAsync(config);

            var publisher = new RedisPublisher(connection);
            // A long heartbeat so no ping can interleave with the events the assertions read.
            var options = new LiveEventStreamOptions { HeartbeatInterval = TimeSpan.FromSeconds(30) };
            var stream = CreateStream(connection, options);

            var beforeOutage = await stream.SubscribeAsync("outage-before", ForChannel("outage"));
            Assert.Equal(LiveEventSubscribeStatus.Ok, beforeOutage.Status);
            await using var established = beforeOutage.Subscription!;

            await PublishAsync(publisher, 1);
            Assert.Equal(1, (await ReadOneAsync(established))?.SessionId);

            await container.StopAsync();
            await WaitUntilAsync(() => !connection.IsConnected, TimeSpan.FromSeconds(30));
            Assert.False(connection.IsConnected);

            // A process that has never subscribed successfully reports the outage honestly — this is
            // the InfrastructureUnavailable path #42 added, and the endpoint's 503.
            var cold = CreateStream(connection, options);
            var coldDuringOutage = await cold.SubscribeAsync("outage-cold", ForChannel("outage"));
            Assert.Equal(LiveEventSubscribeStatus.InfrastructureUnavailable, coldDuringOutage.Status);

            // A process that already holds the Redis subscription answers Ok instead, because the
            // latch skips the check. Asserted, not merely tolerated: the connection it hands out is
            // wired into the same fan-out as every other one and therefore recovers with them, which
            // the rest of this test proves.
            var warmDuringOutage = await stream.SubscribeAsync("outage-warm", ForChannel("outage"));
            Assert.Equal(LiveEventSubscribeStatus.Ok, warmDuringOutage.Status);
            await using var admittedDuringOutage = warmDuringOutage.Subscription!;

            await container.StartAsync();
            await WaitUntilAsync(() => connection.IsConnected, TimeSpan.FromSeconds(60));
            Assert.True(connection.IsConnected);

            // One event for both readers: StackExchange.Redis restored the channel subscription on
            // its own, so neither the pre-outage connection nor the one admitted mid-outage needs any
            // client action to receive again. Nothing was published while Redis was down — Redis
            // pub/sub buffers nothing — so both buffers are empty and this is the next event each of
            // them sees.
            await WaitUntilAsync(async () =>
            {
                await PublishAsync(publisher, 2);
                return true;
            }, TimeSpan.FromSeconds(30));

            Assert.Equal(2, (await ReadOneAsync(established))?.SessionId);
            Assert.Equal(2, (await ReadOneAsync(admittedDuringOutage))?.SessionId);

            // And the cold stream, still latch-less, now genuinely succeeds.
            var coldAfterRecovery = await cold.SubscribeAsync("outage-cold-2", ForChannel("outage"));
            Assert.Equal(LiveEventSubscribeStatus.Ok, coldAfterRecovery.Status);
            await coldAfterRecovery.Subscription!.DisposeAsync();
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    private static RedisLiveEventStream CreateStream(IConnectionMultiplexer connection, LiveEventStreamOptions options) =>
        new(new RedisSubscriber(connection, NullLogger<RedisSubscriber>.Instance),
            NullLogger<RedisLiveEventStream>.Instance,
            options);

    private static Task PublishAsync(IRedisPublisher publisher, int sessionId) =>
        publisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.UsageFlushed, "outage", sessionId).Serialize());

    private static Func<LiveEvent, bool> ForChannel(string channelName) =>
        liveEvent => string.Equals(liveEvent.Channel, channelName, StringComparison.Ordinal);

    // Returns null on timeout rather than throwing, so a failure reports the missing event.
    private static async Task<LiveEvent?> ReadOneAsync(ILiveEventSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        try
        {
            await foreach (var liveEvent in subscription.Events.WithCancellation(timeout.Token))
            {
                return liveEvent;
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through to null.
        }

        return null;
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Redis is still on its way back — keep waiting rather than failing the run.
            }

            await Task.Delay(250);
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
