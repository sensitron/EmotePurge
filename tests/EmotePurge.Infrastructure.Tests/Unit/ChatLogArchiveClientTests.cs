using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Own streaming fake, not the buffering StubHandler from TwitchHelixClientTests (that one is
// private there and returns a StringContent, which would defeat the point of testing a streaming
// reader). LineChunkedStream hands StreamReader exactly one file line per ReadAsync call, so byte
// counting/hashing stays deterministic and line-granular in these tests; StallingStream simulates
// a connection that stops delivering bytes mid-body (Failure Mode "Log-Client Body").
public class ChatLogArchiveClientTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Unit", "TestData", "chatlog-raw-day.txt");

    [Fact]
    public async Task ReadDayAsync_With200AndFixtureBody_ReturnsCompleteWithMatchingDigestAndCounts()
    {
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var expectedDigest = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant();
        var client = CreateClient(new StreamStubHandler(HttpStatusCode.OK, () => new LineChunkedStream(fixtureBytes)), new ChatLogArchiveOptions());

        var received = new List<ChatLogMessage>();
        var result = await client.ReadDayAsync(
            "489111423", new DateOnly(2026, 1, 15), maxBytes: 10_000_000,
            msg => { received.Add(msg); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.Complete, result.Status);
        Assert.Equal(expectedDigest, result.BodySha256Hex);
        Assert.Equal(fixtureBytes.Length, result.BytesReceived);
        Assert.Equal(6, result.MessageCount);
        Assert.Equal(2, result.NonPrivmsgLines);
        Assert.Equal(0, result.MalformedLines);

        // Order preserved: the callback fires in file order, not e.g. grouped by outcome.
        Assert.Equal(6, received.Count);
        Assert.Equal("hey everyone", received[0].Text);
        Assert.Equal("waves hello", received[2].Text); // the ACTION line, unpacked
        Assert.Equal("gg", received[^1].Text);
    }

    [Fact]
    public async Task ReadDayAsync_With404_ReturnsNoLogDay_WithoutInvokingCallback()
    {
        var client = CreateClient(new FixedStatusStubHandler(HttpStatusCode.NotFound), new ChatLogArchiveOptions());
        var callbackInvoked = false;

        var result = await client.ReadDayAsync(
            "1", new DateOnly(2026, 1, 1), 1000, _ => { callbackInvoked = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.NoLogDay, result.Status);
        Assert.False(callbackInvoked);
        Assert.Equal((int)HttpStatusCode.NotFound, result.HttpStatusCode);
        Assert.Null(result.BodySha256Hex);
    }

    [Fact]
    public async Task ReadDayAsync_With429_ReturnsRateLimited_WithoutReadingBody()
    {
        var client = CreateClient(new FixedStatusStubHandler(HttpStatusCode.TooManyRequests), new ChatLogArchiveOptions());
        var callbackInvoked = false;

        var result = await client.ReadDayAsync(
            "1", new DateOnly(2026, 1, 1), 1000, _ => { callbackInvoked = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.RateLimited, result.Status);
        Assert.False(callbackInvoked);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, result.HttpStatusCode);
    }

    [Fact]
    public async Task ReadDayAsync_WithBodyThatStalls_ReturnsBodyTimeout_WithoutHanging()
    {
        var handler = new StreamStubHandler(HttpStatusCode.OK, () => new StallingStream(Encoding.UTF8.GetBytes("@partial"), stallAfterBytes: 4));
        var options = new ChatLogArchiveOptions { BodyTimeout = TimeSpan.FromMilliseconds(300) };
        var client = CreateClient(handler, options);

        var sw = Stopwatch.StartNew();
        var result = await client.ReadDayAsync("1", new DateOnly(2026, 1, 1), 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);
        sw.Stop();

        Assert.Equal(ChatLogDayStatus.BodyTimeout, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"expected the body-timeout to fire quickly, took {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadDayAsync_WithMaxBytesSmallerThanBody_ReturnsByteCapExceeded_AfterOneCompleteLineOfSlack()
    {
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var firstLineLength = Array.IndexOf(fixtureBytes, (byte)'\n') + 1;
        var secondLineLength = Array.IndexOf(fixtureBytes, (byte)'\n', firstLineLength) + 1 - firstLineLength;
        var client = CreateClient(new StreamStubHandler(HttpStatusCode.OK, () => new LineChunkedStream(fixtureBytes)), new ChatLogArchiveOptions());

        var maxBytes = firstLineLength + 20; // smaller than the first two lines combined
        var received = new List<ChatLogMessage>();
        var result = await client.ReadDayAsync(
            "1", new DateOnly(2026, 1, 1), maxBytes, msg => { received.Add(msg); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.ByteCapExceeded, result.Status);
        Assert.Null(result.BodySha256Hex);
        // The client stops right after the line that pushed it over the cap — never more than one
        // extra line's worth of slack beyond maxBytes.
        Assert.Equal(firstLineLength + secondLineLength, result.BytesReceived);
        Assert.True(result.BytesReceived <= maxBytes + secondLineLength);
        Assert.Single(received); // the first (fully within-budget) line was still parsed and delivered
    }

    [Fact]
    public async Task ReadDayAsync_WithCallerCancellationMidBody_ThrowsOperationCanceledException_NotBodyTimeout()
    {
        var handler = new StreamStubHandler(HttpStatusCode.OK, () => new StallingStream(Encoding.UTF8.GetBytes("@partial"), stallAfterBytes: 4));
        var options = new ChatLogArchiveOptions { BodyTimeout = TimeSpan.FromSeconds(30) };
        var client = CreateClient(handler, options);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReadDayAsync("1", new DateOnly(2026, 1, 1), 10_000_000, _ => ValueTask.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task ReadDayAsync_CalledTwiceInARow_WaitsAtLeastRequestDelayBetweenStarts()
    {
        var options = new ChatLogArchiveOptions { RequestDelay = TimeSpan.FromMilliseconds(300) };
        var client = CreateClient(new FixedStatusStubHandler(HttpStatusCode.NotFound), options);

        var sw = Stopwatch.StartNew();
        await client.ReadDayAsync("1", new DateOnly(2026, 1, 1), 1000, _ => ValueTask.CompletedTask, CancellationToken.None);
        await client.ReadDayAsync("1", new DateOnly(2026, 1, 2), 1000, _ => ValueTask.CompletedTask, CancellationToken.None);
        sw.Stop();

        Assert.True(
            sw.Elapsed >= options.RequestDelay - TimeSpan.FromMilliseconds(50),
            $"the second call should not start sooner than RequestDelay after the first, only waited {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadDayAsync_WithMostlyUnparsableLines_ReturnsMalformedResponse()
    {
        var body = string.Join('\n', Enumerable.Repeat("this is not an irc line at all", 6)) + "\n";
        var handler = new StreamStubHandler(HttpStatusCode.OK, () => new LineChunkedStream(Encoding.UTF8.GetBytes(body)));
        var client = CreateClient(handler, new ChatLogArchiveOptions());

        var result = await client.ReadDayAsync("1", new DateOnly(2026, 1, 1), 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.MalformedResponse, result.Status);
        Assert.Null(result.BodySha256Hex);
        Assert.Equal(0, result.MessageCount);
        Assert.Equal(0, result.NonPrivmsgLines);
        Assert.Equal(6, result.MalformedLines);
    }

    [Fact]
    public async Task ReadDayAsync_WithTransportErrorMidBody_ReturnsTransportFailure()
    {
        var handler = new StreamStubHandler(HttpStatusCode.OK, () => new ThrowingAfterBytesStream(Encoding.UTF8.GetBytes("@partial-line-before-drop"), throwAfterBytes: 5));
        var client = CreateClient(handler, new ChatLogArchiveOptions());

        var result = await client.ReadDayAsync("1", new DateOnly(2026, 1, 1), 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.TransportFailure, result.Status);
        Assert.Null(result.BodySha256Hex);
    }

    private static ChatLogArchiveClient CreateClient(HttpMessageHandler handler, ChatLogArchiveOptions options)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://logs.example.test/") };
        return new ChatLogArchiveClient(httpClient, options, new RecordingLogger<ChatLogArchiveClient>());
    }

    private sealed class FixedStatusStubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class StreamStubHandler(HttpStatusCode statusCode, Func<Stream> streamFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode) { Content = new StreamContent(streamFactory()) };
            return Task.FromResult(response);
        }
    }

    // Hands StreamReader exactly one source line (including its trailing '\n') per ReadAsync call,
    // so the client's line-granular byte counting/hashing behaves deterministically in tests
    // instead of depending on however much a real network stream happens to buffer ahead.
    private sealed class LineChunkedStream(byte[] body) : Stream
    {
        private readonly List<(int Offset, int Length)> _lines = SplitIntoLines(body);
        private int _lineIndex;
        private int _offsetInLine;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_lineIndex >= _lines.Count)
            {
                return ValueTask.FromResult(0);
            }

            var (offset, length) = _lines[_lineIndex];
            var remaining = length - _offsetInLine;
            var toCopy = Math.Min(remaining, buffer.Length);
            body.AsSpan(offset + _offsetInLine, toCopy).CopyTo(buffer.Span);
            _offsetInLine += toCopy;
            if (_offsetInLine >= length)
            {
                _lineIndex++;
                _offsetInLine = 0;
            }

            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static List<(int Offset, int Length)> SplitIntoLines(byte[] body)
        {
            var result = new List<(int, int)>();
            var start = 0;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == (byte)'\n')
                {
                    result.Add((start, i - start + 1));
                    start = i + 1;
                }
            }

            if (start < body.Length)
            {
                result.Add((start, body.Length - start));
            }

            return result;
        }
    }

    // Delivers `stallAfterBytes` bytes normally, then hangs on every subsequent read until the
    // caller's token cancels — simulating a connection that stops sending mid-body without closing
    // (Failure Mode "Log-Client Body": "Aggregator-Instanz stockt mitten in 16 MB").
    private sealed class StallingStream(byte[] body, int stallAfterBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var cap = Math.Min(stallAfterBytes, body.Length);
            if (_position >= cap)
            {
                // Never completes on its own — only the caller's token (directly, or via the
                // client's body-timeout CTS linked to it) can end this.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            var toCopy = Math.Min(buffer.Length, cap - _position);
            body.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Delivers `throwAfterBytes` bytes normally, then throws IOException — simulating a dropped
    // connection mid-body, distinct from a stall (the socket errors out instead of going silent).
    private sealed class ThrowingAfterBytesStream(byte[] body, int throwAfterBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= throwAfterBytes)
            {
                throw new IOException("Simulated connection drop mid-body.");
            }

            var toCopy = Math.Min(buffer.Length, throwAfterBytes - _position);
            body.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
