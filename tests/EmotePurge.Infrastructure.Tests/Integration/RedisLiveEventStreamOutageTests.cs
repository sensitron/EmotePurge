using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
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
        // A fixed host port is load-bearing, not incidental: measured directly against this Docker
        // (2026-09-01, Docker 29.7.2) before writing this fix, Testcontainers' random mapping — no
        // WithPortBinding — allocates a *new* host port on every StartAsync of the same container
        // (empty HostConfig.PortBindings, i.e. Docker's `-P` behaviour), so the multiplexer created
        // before the outage would have nothing to reconnect to after `container.StartAsync()` below.
        // A fixed binding's HostConfig.PortBindings entry, in the same measurement, survived
        // stop/start unchanged — that is what the outage-and-recovery below actually needs.
        var container = await StartRedisContainerAsync();
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

            // IsConnected only says the physical server connection is back, not that
            // StackExchange.Redis has re-registered our channel subscription with that server yet —
            // PUBLISH succeeds even with zero subscribers, so a single blind publish-then-read right
            // after IsConnected flips is exactly the race that made this test flaky. Instead, publish
            // and read in a loop with a fresh SessionId each attempt until one is actually observed:
            // an attempt in the still-unsubscribed window reaches nobody (Redis drops it, it does not
            // queue), so there is no risk of a stale SessionId confusing a later attempt.
            var recoveredSessionId = await PublishUntilDeliveredAsync(publisher, established, TimeSpan.FromSeconds(30));

            // No separate wait for the second reader: OnMessageAsync offers one delivered message to
            // every open subscription in the same synchronous pass, so the publish that
            // PublishUntilDeliveredAsync just proved landed on `established` landed on
            // `admittedDuringOutage` at the same time. Asserting the identical SessionId (rather than
            // just "some event arrived") is the "both received after recovery" proof: it rules out
            // this read picking up some other, unrelated event.
            Assert.Equal(recoveredSessionId, (await ReadOneAsync(admittedDuringOutage))?.SessionId);

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

    /// <summary>
    /// A fixed port is required (see the comment at the call site), but picking one ourselves is an
    /// inherent TOCTOU race against every other test process doing the same thing: <see cref="FreeTcpPort"/>
    /// closes its probe listener before Docker binds the port, and something else can claim it in
    /// between. Rather than eliminate the race — not possible without asking Docker itself for a free
    /// port, which is exactly what the plain random-mapping builder does and which breaks reconnection
    /// (see the call site) — this retries with a freshly picked port on the specific failure that race
    /// produces.
    /// </summary>
    private static async Task<RedisContainer> StartRedisContainerAsync()
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var container = new RedisBuilder("redis:7.2-alpine")
                .WithPortBinding(FreeTcpPort(), 6379)
                .Build();

            try
            {
                await container.StartAsync();
                return container;
            }
            catch (DockerApiException ex) when (attempt < maxAttempts &&
                ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                await container.DisposeAsync();
            }
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

    private static RedisLiveEventStream CreateStream(IConnectionMultiplexer connection, LiveEventStreamOptions options) =>
        new(new RedisSubscriber(connection, NullLogger<RedisSubscriber>.Instance),
            NullLogger<RedisLiveEventStream>.Instance,
            options);

    private static Task PublishAsync(IRedisPublisher publisher, int sessionId) =>
        publisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.UsageFlushed, "outage", sessionId).Serialize());

    private static Func<LiveEvent, bool> ForChannel(string channelName) =>
        liveEvent => string.Equals(liveEvent.Channel, channelName, StringComparison.Ordinal);

    /// <summary>
    /// Publishes with a fresh, ever-increasing SessionId and reads once from <paramref name="subscription"/>
    /// per attempt, until the read observes the very SessionId that attempt published — proof that the
    /// channel subscription is live end-to-end, not just that the publish call itself did not throw.
    /// Retries rather than a single publish-then-read because a publish while StackExchange.Redis is
    /// still re-registering the subscription server-side reaches no one and is not queued for later.
    /// </summary>
    private static async Task<long> PublishUntilDeliveredAsync(
        IRedisPublisher publisher, ILiveEventSubscription subscription, TimeSpan timeout)
    {
        // Starts above the pre-outage SessionId (1) so a leftover from before the outage can never be
        // mistaken for a delivery this loop caused.
        var sessionId = 1;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            sessionId++;
            try
            {
                await PublishAsync(publisher, sessionId);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Reconnected but not yet ready to accept commands — try again below.
            }

            var received = await ReadOneAsync(subscription, TimeSpan.FromSeconds(2));
            if (received?.SessionId == sessionId)
            {
                return sessionId;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    $"Nach der Redis-Wiederherstellung kam innerhalb von {timeout} kein Live-Event auf der Subscription an.");
            }
        }
    }

    private static Task<LiveEvent?> ReadOneAsync(ILiveEventSubscription subscription) =>
        ReadOneAsync(subscription, ReadTimeout);

    // Returns null on timeout rather than throwing, so a failure reports the missing event.
    private static async Task<LiveEvent?> ReadOneAsync(ILiveEventSubscription subscription, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var liveEvent in subscription.Events.WithCancellation(timeoutSource.Token))
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
}
