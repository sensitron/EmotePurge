using System.Net;
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
/// Issue #128's second half: <c>DELETE /api/live/connections/{connectionId}</c> lets the client end
/// its own stream on request, instead of the slot release depending on a proxy chain (Cloudflare -&gt;
/// nginx) noticing a cancelled request on its own — production measurement showed that can take
/// 15-25 s even with the keepalive fix (issue #128's first half, see <see cref="LiveStreamKeepaliveTests"/>)
/// in place. Drives the real <c>WebApplicationFactory</c> pipeline the same way that class does,
/// reusing the same Channel-backed fake subscription shape for a genuinely async, controllable event
/// source rather than a canned NSubstitute return value.
/// </summary>
public sealed class LiveStreamConnectionReleaseTests : IClassFixture<ApiFactory>
{
    private const string LiveEventsPath = "/api/channels/live-events";
    private const string ConnectionsPathPrefix = "/api/live/connections/";

    private readonly ApiFactory _factory;

    public LiveStreamConnectionReleaseTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FirstFrame_CarriesA32HexConnectionId_LaterFramesCarryNone()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        using var request = NewStreamRequest("release-first-frame");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var firstFrame = await ReadNextFrameAsync(reader, timeout.Token);
        Assert.NotNull(firstFrame.Id);
        Assert.Matches("^[0-9a-f]{32}$", firstFrame.Id);

        var secondFrame = await ReadNextFrameAsync(reader, timeout.Token);
        Assert.Null(secondFrame.Id);
    }

    [Fact]
    public async Task Delete_WithOwnConnectionId_EndsTheStreamPromptly_AndDisposesTheSubscription()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        var (connectionId, streamResponse, streamReader) =
            await OpenStreamAndReadConnectionIdAsync(client, "release-own-id");
        using (streamResponse)
        using (streamReader)
        {
            using var deleteRequest = NewDeleteRequest(connectionId, "release-own-id");
            using var deleteResponse = await client.SendAsync(deleteRequest);

            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            await WaitForDisposalAsync(subscription);
            Assert.True(subscription.Disposed);
        }
    }

    [Fact]
    public async Task Delete_WithAnotherLoginsConnectionId_LeavesThatStreamRunning()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        var (connectionId, streamResponse, streamReader) =
            await OpenStreamAndReadConnectionIdAsync(client, "release-victim");
        using (streamResponse)
        using (streamReader)
        {
            using var deleteRequest = NewDeleteRequest(connectionId, "release-attacker");
            using var deleteResponse = await client.SendAsync(deleteRequest);

            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            // Give a (wrongly hoped-for) cancellation a moment it would need if it had gone through,
            // then prove the stream is genuinely still alive — not merely "not yet noticed" — by
            // publishing a real event and reading it back.
            await Task.Delay(200);
            Assert.False(subscription.Disposed);

            var expected = new LiveEvent(LiveEvents.LiveChanged);
            subscription.Publish(expected);

            // The 50 ms keepalive interval may also have a ping frame already queued up ahead of the
            // real event — skip those, the same way LiveStreamKeepaliveTests' RealEvent_PassesThroughVerbatim
            // sidesteps the race by using a much longer interval instead.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            string? payload;
            do
            {
                payload = (await ReadNextFrameAsync(streamReader, timeout.Token)).Data;
            }
            while (payload == LiveEvent.Heartbeat.Serialize());

            Assert.Equal(expected.Serialize(), payload);
        }
    }

    [Fact]
    public async Task Delete_WithUnknownConnectionId_Answers204()
    {
        using var client = _factory.CreateClient();
        using var request = NewDeleteRequest(new string('a', 32), "release-unknown");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_TwiceForTheSameId_BothAnswer204_AndTheSecondIsANoOp()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        var (connectionId, streamResponse, streamReader) =
            await OpenStreamAndReadConnectionIdAsync(client, "release-twice");
        using (streamResponse)
        using (streamReader)
        {
            using var firstDelete = NewDeleteRequest(connectionId, "release-twice");
            using var firstResponse = await client.SendAsync(firstDelete);
            Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

            using var secondDelete = NewDeleteRequest(connectionId, "release-twice");
            using var secondResponse = await client.SendAsync(secondDelete);
            Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);
        }
    }

    [Fact]
    public async Task Delete_Unauthenticated_Answers401()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, ConnectionsPathPrefix + new string('a', 32));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithAMalformedId_IsNotRoutedToTheHandler()
    {
        using var client = _factory.CreateClient();
        using var request = NewDeleteRequest("not-hex", "release-malformed");
        using var response = await client.SendAsync(request);

        // Whatever routing answers for "no match" (currently a bare 404) — the one thing this proves
        // is that the handler, which always answers 204, was never reached.
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Registry_ForgetsAConnection_OnceItsStreamHasEnded()
    {
        var subscription = new FakeLiveEventSubscription();
        SubscribeReturns(subscription);

        using var factory = WithKeepaliveInterval(TimeSpan.FromMilliseconds(50));
        using var client = factory.CreateClient();
        var (connectionId, streamResponse, streamReader) =
            await OpenStreamAndReadConnectionIdAsync(client, "release-leak-check");

        // Ends the stream the ordinary way — a client abort — rather than through the DELETE endpoint
        // under test elsewhere in this file: this proves the registry entry is cleaned up on an exit
        // path other than the one the endpoint drives (see LiveStreamConnectionRegistry.Register).
        // Which path exactly: over an HTTP round trip the server side has, in practice, already
        // written past the id-carrying first frame and is suspended inside StreamAsync's inner
        // try/loop (awaiting the next real event or keepalive tick) by the time this abort reaches it
        // — the same path LiveStreamKeepaliveTests.ClientAbort_DisposesTheSubscription exercises, where
        // the pre-existing inner finally's lifetime.Cancel() already did the job. It does NOT reliably
        // hit "aborted while still suspended at the very first yield, before the inner try is ever
        // entered" — that narrower, previously-leaking path has no clean HTTP-level trigger (there is
        // no way to force the abort to land in that exact window from the client side) and is instead
        // covered directly by LiveStreamAsyncDisposalTests.Dispose_RightAfterTheFirstYield_*, which
        // drives StreamAsync itself to exactly that suspension point.
        streamReader.Dispose();
        (await streamResponse.Content.ReadAsStreamAsync()).Dispose();
        streamResponse.Dispose();

        await WaitForDisposalAsync(subscription);
        Assert.True(subscription.Disposed);

        // The registry singleton is shared by the whole host; releasing the same id again must now be
        // the "unknown id" no-op, which is only true if the entry is actually gone rather than merely
        // quiet about a live one.
        var registry = factory.Services.GetRequiredService<LiveStreamConnectionRegistry>();
        Assert.False(registry.TryRelease(connectionId, "release-leak-check"));
    }

    private void SubscribeReturns(FakeLiveEventSubscription subscription) =>
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(LiveEventSubscribeResult.Ok(subscription));

    private WebApplicationFactory<Program> WithKeepaliveInterval(TimeSpan interval) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton(new LiveStreamKeepaliveOptions { KeepaliveInterval = interval })));

    private static HttpRequestMessage NewStreamRequest(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LiveEventsPath);
        AddAuth(request, userId);
        return request;
    }

    private static HttpRequestMessage NewDeleteRequest(string connectionId, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, ConnectionsPathPrefix + connectionId);
        AddAuth(request, userId);
        return request;
    }

    private static void AddAuth(HttpRequestMessage request, string userId)
    {
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
    }

    private static async Task<(string ConnectionId, HttpResponseMessage Response, StreamReader Reader)>
        OpenStreamAndReadConnectionIdAsync(HttpClient client, string userId)
    {
        var request = NewStreamRequest(userId);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var firstFrame = await ReadNextFrameAsync(reader, timeout.Token);
        Assert.NotNull(firstFrame.Id);
        return (firstFrame.Id!, response, reader);
    }

    private static async Task WaitForDisposalAsync(FakeLiveEventSubscription subscription)
    {
        using var disposed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!subscription.Disposed && !disposed.IsCancellationRequested)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Reads one SSE frame's <c>id:</c> and <c>data:</c> fields (either order — both appear in the
    /// wire output, but not in a fixed order — up to the blank line that terminates the frame).
    /// </summary>
    private static async Task<(string? Id, string? Data)> ReadNextFrameAsync(StreamReader reader, CancellationToken ct)
    {
        string? id = null;
        string? data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return (id, data);
            }

            if (line.Length == 0)
            {
                if (data is not null)
                {
                    return (id, data);
                }

                continue;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line["id: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
        }
    }

    /// <summary>
    /// Same shape as the private fake in <see cref="LiveStreamKeepaliveTests"/>: a
    /// <see cref="Channel{T}"/>-backed subscription rather than an NSubstitute mock, because the
    /// behaviour under test needs a genuinely async, controllable event source.
    /// </summary>
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
