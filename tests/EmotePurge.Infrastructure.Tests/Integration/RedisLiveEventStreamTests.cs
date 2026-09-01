using System.Diagnostics;
using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Runs against a real redis:7.2-alpine container: the whole point of this class is the interplay of
// StackExchange.Redis pub/sub delivery, the per-connection bounded channel and the heartbeat timing —
// none of which a mocked IRedisSubscriber would exercise. Every test publishes onto its own channel
// name so the shared Redis channel cannot leak events between them.
[Collection("Redis")]
public class RedisLiveEventStreamTests(RedisFixture fixture)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private RedisLiveEventStream CreateStream(LiveEventStreamOptions? options = null) =>
        new(new RedisSubscriber(fixture.Connection, NullLogger<RedisSubscriber>.Instance),
            NullLogger<RedisLiveEventStream>.Instance,
            options);

    private Task PublishAsync(LiveEvent liveEvent) =>
        new RedisPublisher(fixture.Connection).PublishAsync(LiveEvents.Channel, liveEvent.Serialize());

    private Task PublishRawAsync(string payload) =>
        new RedisPublisher(fixture.Connection).PublishAsync(LiveEvents.Channel, payload);

    // Asserts the Ok case and unwraps the subscription — SubscribeAsync returns a
    // LiveEventSubscribeResult since #42, not a bare ILiveEventSubscription?, so every happy-path call
    // site needs one line to get from the result to the disposable it wraps.
    private static ILiveEventSubscription RequireOk(LiveEventSubscribeResult result)
    {
        Assert.Equal(LiveEventSubscribeStatus.Ok, result.Status);
        Assert.NotNull(result.Subscription);
        return result.Subscription!;
    }

    [Fact]
    public async Task PublishedEvent_ReachesTheSubscriber()
    {
        var stream = CreateStream();
        await using var subscription = RequireOk(await stream.SubscribeAsync("user-1", ForChannel("reach-1")));

        await PublishAsync(new LiveEvent(LiveEvents.UsageFlushed, "reach-1"));

        var received = Assert.Single(await ReadAsync(subscription, 1));
        Assert.Equal(LiveEvents.UsageFlushed, received.Type);
        Assert.Equal("reach-1", received.Channel);
    }

    [Fact]
    public async Task Filter_ExcludesNonMatchingEvents()
    {
        var stream = CreateStream();
        await using var subscription = RequireOk(await stream.SubscribeAsync("user-2", ForChannel("filter-mine")));

        // The unwanted one first: if the filter leaked, the very first item read would be it.
        await PublishAsync(new LiveEvent(LiveEvents.UsageFlushed, "filter-other"));
        await PublishAsync(new LiveEvent(LiveEvents.VoteChanged, "filter-mine", 5));

        var received = Assert.Single(await ReadAsync(subscription, 1));
        Assert.Equal("filter-mine", received.Channel);
        Assert.Equal(5, received.SessionId);
    }

    [Fact]
    public async Task TwoSubscribers_EachReceiveTheirOwnCopy()
    {
        var stream = CreateStream();
        await using var first = RequireOk(await stream.SubscribeAsync("user-3a", ForChannel("fanout")));
        await using var second = RequireOk(await stream.SubscribeAsync("user-3b", ForChannel("fanout")));

        await PublishAsync(new LiveEvent(LiveEvents.ChannelSynced, "fanout"));

        Assert.Equal("fanout", Assert.Single(await ReadAsync(first, 1)).Channel);
        Assert.Equal("fanout", Assert.Single(await ReadAsync(second, 1)).Channel);
    }

    [Fact]
    public async Task Dispose_RemovesTheSubscriber_AndLeavesPublishingIntact()
    {
        var options = new LiveEventStreamOptions { MaxPerSubscriber = 1 };
        var stream = CreateStream(options);

        var closed = RequireOk(await stream.SubscribeAsync("user-4", ForChannel("dispose")));
        await closed.DisposeAsync();

        // The slot is free again — proof the fan-out entry is gone, not just its reader.
        await using var reopened = RequireOk(await stream.SubscribeAsync("user-4", ForChannel("dispose")));

        await PublishAsync(new LiveEvent(LiveEvents.UsageFlushed, "dispose"));

        Assert.Equal("dispose", Assert.Single(await ReadAsync(reopened, 1)).Channel);
    }

    [Fact]
    public async Task SubscribeAsync_ReturnsQuotaExhausted_WhenTheSameSubscriberHitsItsLimit()
    {
        var stream = CreateStream(new LiveEventStreamOptions { MaxPerSubscriber = 2 });

        await using var first = RequireOk(await stream.SubscribeAsync("user-5", _ => true));
        await using var second = RequireOk(await stream.SubscribeAsync("user-5", _ => true));
        var third = await stream.SubscribeAsync("user-5", _ => true);

        // QuotaExhausted rather than an exception or a lazily-failing enumerable: the endpoint has to
        // answer 429 before it writes the first response byte.
        Assert.Equal(LiveEventSubscribeStatus.QuotaExhausted, third.Status);
        Assert.Null(third.Subscription);

        // Another identity is unaffected by one account exhausting its own budget.
        await using var other = RequireOk(await stream.SubscribeAsync("user-5-other", _ => true));
    }

    [Fact]
    public async Task SubscribeAsync_ReturnsQuotaExhausted_WhenTheProcessWideLimitIsReached()
    {
        var stream = CreateStream(new LiveEventStreamOptions { MaxSubscriptions = 2, MaxPerSubscriber = 5 });

        await using var first = RequireOk(await stream.SubscribeAsync("user-6a", _ => true));
        await using var second = RequireOk(await stream.SubscribeAsync("user-6b", _ => true));

        var third = await stream.SubscribeAsync("user-6c", _ => true);
        Assert.Equal(LiveEventSubscribeStatus.QuotaExhausted, third.Status);
        Assert.Null(third.Subscription);
    }

    [Fact]
    public async Task MalformedPayload_IsDropped_WithoutTearingDownTheSubscription()
    {
        var stream = CreateStream();
        await using var subscription = RequireOk(await stream.SubscribeAsync("user-7", ForChannel("garbage")));

        await PublishRawAsync("this is not json");
        await PublishRawAsync("""{"noTypeHere":true}""");
        await PublishRawAsync("[1,2,3]");
        await PublishAsync(new LiveEvent(LiveEvents.UsageFlushed, "garbage"));

        var received = Assert.Single(await ReadAsync(subscription, 1));
        Assert.Equal(LiveEvents.UsageFlushed, received.Type);
    }

    [Fact]
    public async Task Heartbeat_IsInjected_WhileTheStreamIsIdle()
    {
        // 50 ms instead of the production 15 s — the whole reason the interval is a constructor
        // parameter rather than a constant.
        var stream = CreateStream(new LiveEventStreamOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(50) });
        await using var subscription = RequireOk(await stream.SubscribeAsync("user-8", _ => false));

        var received = await ReadAsync(subscription, 3);

        Assert.Equal(3, received.Count);
        Assert.All(received, liveEvent =>
        {
            Assert.Equal(LiveEvents.Ping, liveEvent.Type);
            Assert.Null(liveEvent.Channel);
        });
    }

    [Fact]
    public async Task Backpressure_DropsOldestEvents_AndNeverBlocksThePublisher()
    {
        // Long heartbeat so no ping can slip between the events and confuse the ordering assertions.
        var stream = CreateStream(new LiveEventStreamOptions { HeartbeatInterval = TimeSpan.FromSeconds(30) });
        await using var subscription = RequireOk(await stream.SubscribeAsync("user-9", ForChannel("backpressure")));

        // 100 events into a 64-slot buffer that nobody is reading from.
        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i <= 100; i++)
        {
            await PublishAsync(new LiveEvent(LiveEvents.VoteChanged, "backpressure", i));
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Publishing blocked for {stopwatch.Elapsed}.");

        // Give delivery time to finish before the reader starts, so what is left in the buffer is
        // the steady-state result of the drop policy rather than a race with it.
        await Task.Delay(750);

        var received = await ReadAsync(subscription, 64);

        Assert.Equal(64, received.Count);
        // The newest survive, the oldest are gone — correct for stale-notifications, because the
        // client refetches full state on the newest one anyway.
        Assert.Equal(100, received[^1].SessionId);
        Assert.DoesNotContain(received, liveEvent => liveEvent.SessionId == 1);
    }

    private static Func<LiveEvent, bool> ForChannel(string channelName) =>
        liveEvent => string.Equals(liveEvent.Channel, channelName, StringComparison.Ordinal);

    // Returns whatever arrived before the timeout instead of throwing, so a failing test reports the
    // missing event rather than a cancellation.
    private static async Task<List<LiveEvent>> ReadAsync(ILiveEventSubscription subscription, int count)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        var received = new List<LiveEvent>();
        await foreach (var liveEvent in subscription.Events.WithCancellation(timeout.Token))
        {
            received.Add(liveEvent);
            if (received.Count == count)
            {
                break;
            }
        }

        return received;
    }
}
