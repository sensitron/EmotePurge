using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using EmotePurge.Api.Endpoints;
using EmotePurge.Core.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Issue #128: a proxy chain (Cloudflare -&gt; nginx) only propagates a browser's cancel to the origin
/// when the origin next writes, and until this fix nothing was written on <c>LiveEndpoints.StreamAsync</c>
/// until Infrastructure's own 15 s heartbeat — so an abandoned stream held its connection-budget slot
/// for up to that long. These tests drive the real <c>WebApplicationFactory</c> pipeline against
/// <c>/api/channels/live-events</c> with a hand-written <see cref="ILiveEventSubscription"/> fake — a
/// <see cref="Channel{T}"/>-backed one rather than an NSubstitute mock, because the behaviour under
/// test needs a genuinely async, controllable event source (publish an event whenever the test wants
/// one, never complete unless told to) rather than a canned return value.
/// </summary>
public sealed class LiveStreamKeepaliveTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/channels/live-events";

    private readonly ApiFactory _factory;

    public LiveStreamKeepaliveTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The default 5 s keepalive interval is left in place on purpose here: reading the first frame
    /// well inside a much shorter budget is exactly what proves it did not wait on either the
    /// keepalive or a real event, only on the stream opening.
    /// </summary>
    [Fact]
    public async Task FirstFrame_ArrivesImmediately_WellBeforeAnyKeepaliveWouldBeDue()
    {
        SubscribeReturns(new FakeLiveEventSubscription());

        using var client = _factory.CreateClient();
        using var request = NewRequest("keepalive-first-frame");

        var started = DateTime.UtcNow;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var payload = await ReadNextDataLineAsync(reader, timeout.Token);
        var elapsed = DateTime.UtcNow - started;

        var liveEvent = LiveEvent.TryParse(payload);
        Assert.NotNull(liveEvent);
        Assert.Equal(LiveEvents.Ping, liveEvent!.Type);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"First frame took {elapsed}, expected well under the 5 s keepalive interval.");
    }

    [Fact]
    public async Task NoEvents_KeepaliveFramesArriveRepeatedly_AtTheConfiguredInterval()
    {
        SubscribeReturns(new FakeLiveEventSubscription());

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        using var request = NewRequest("keepalive-repeat");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // The immediate first frame plus at least three keepalive ticks — comfortably more than one,
        // so a single coincidental ping cannot make this pass by luck.
        for (var i = 0; i < 4; i++)
        {
            var payload = await ReadNextDataLineAsync(reader, timeout.Token);
            var liveEvent = LiveEvent.TryParse(payload);
            Assert.Equal(LiveEvents.Ping, liveEvent!.Type);
        }
    }

    [Fact]
    public async Task RealEvent_PassesThroughVerbatim()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromSeconds(30));
        using var client = factory.CreateClient();
        using var request = NewRequest("keepalive-real-event");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // The immediate first frame — a heartbeat, and not what this test is about.
        await ReadNextDataLineAsync(reader, timeout.Token);

        var expected = new LiveEvent(LiveEvents.LiveChanged);
        subscription.Publish(expected);

        var payload = await ReadNextDataLineAsync(reader, timeout.Token);
        Assert.Equal(expected.Serialize(), payload);
    }

    [Fact]
    public async Task ClientAbort_DisposesTheSubscription()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        using var request = NewRequest("keepalive-abort");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        var reader = new StreamReader(stream);

        using (var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            await ReadNextDataLineAsync(reader, readTimeout.Token); // the immediate first frame
        }

        // Simulates a browser-side route change aborting the EventSource mid-stream: disposing an
        // in-flight response/body is what a real client does when it gives up before the stream ends,
        // and is what must reach HttpContext.RequestAborted server-side and, through it, the
        // subscription's await-using disposal — the slot release this whole issue is about.
        reader.Dispose();
        stream.Dispose();
        response.Dispose();

        using var disposed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!subscription.Disposed && !disposed.IsCancellationRequested)
        {
            await Task.Delay(20);
        }

        Assert.True(subscription.Disposed);
    }

    [Fact]
    public async Task QuotaExhausted_CarriesTheLoweredRetryAfterValue()
    {
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.QuotaExhausted));

        using var client = _factory.CreateClient();
        using var request = NewRequest("keepalive-quota");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("10", Assert.Single(response.Headers.GetValues("Retry-After")));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(10, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    private void SubscribeReturns(FakeLiveEventSubscription subscription) =>
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(LiveEventSubscribeResult.Ok(subscription));

    private WebApplicationFactory<Program> WithKeepaliveInterval(TimeSpan interval) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton(new LiveStreamKeepaliveOptions { KeepaliveInterval = interval })));

    private static HttpRequestMessage NewRequest(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return request;
    }

    /// <summary>
    /// SSE frames from <c>SseFormatter</c> are <c>data: &lt;payload&gt;\n\n</c> lines (no named event
    /// type here, see <c>LiveEndpoints.StreamAsync</c>'s doc comment) — this pulls out the payload of
    /// the next one, skipping the blank separator lines in between.
    /// </summary>
    private static async Task<string?> ReadNextDataLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line["data: ".Length..];
            }
        }
    }

    private sealed class FakeLiveEventSubscription : ILiveEventSubscription
    {
        private readonly Channel<LiveEvent> _channel = Channel.CreateUnbounded<LiveEvent>();

        public bool Disposed { get; private set; }

        public IAsyncEnumerable<LiveEvent> Events => _channel.Reader.ReadAllAsync();

        public void Publish(LiveEvent liveEvent) => _channel.Writer.TryWrite(liveEvent);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
