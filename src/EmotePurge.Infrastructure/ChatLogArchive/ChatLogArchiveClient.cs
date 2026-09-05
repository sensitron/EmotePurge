using System.Net;
using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.ChatLogArchive;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>
/// Streams one channel-day of chat history from the justlog-compatible <c>?raw</c> text endpoint
/// (measured live 2026-09-05, T8) instead of buffering the whole body — a large channel's day can
/// run into the tens of megabytes.
/// <para>
/// <b>Strictly sequential.</b> This client issues at most one request at a time and waits at
/// least <see cref="ChatLogArchiveOptions.RequestDelay"/> between the start of consecutive
/// requests, tracked as instance state on <see cref="_lastRequestStartedAtTicks"/>. A second,
/// concurrent call while one is already in flight is a caller error — there is no internal lock
/// enforcing it.
/// </para>
/// <para>
/// <b>No retry, no rate limiter.</b> The archive documents no contract (Premise 5 of the design):
/// every non-2xx response and every transport failure is reported once via
/// <see cref="ChatLogDayResult"/> and left to the caller (the harness, Task 6) to decide whether
/// to resume this day later.
/// </para>
/// <para>
/// <b>Two timeouts, two jobs.</b> The typed <c>HttpClient</c>'s own <c>Timeout</c> only covers the
/// header phase — <c>SendAsync</c> is called with <c>HttpCompletionOption.ResponseHeadersRead</c>,
/// so it returns as soon as headers arrive. Reading the body afterwards is governed by its own
/// <see cref="ChatLogArchiveOptions.BodyTimeout"/>-bounded <see cref="CancellationTokenSource"/>,
/// linked to the caller's token: a body timeout maps to <see cref="ChatLogDayStatus.BodyTimeout"/>,
/// while a caller cancellation is left to propagate as <see cref="OperationCanceledException"/>
/// instead — the two are told apart via <c>ct.IsCancellationRequested</c>.
/// </para>
/// </summary>
public class ChatLogArchiveClient(
    HttpClient httpClient, ChatLogArchiveOptions options, ILogger<ChatLogArchiveClient> logger) : IChatLogArchiveClient
{
    // A day whose lines are more than half unreadable as any recognized IRC command means the wire
    // format itself changed underneath this client (Failure Mode "Wurzelform anders als
    // angenommen"), not that the channel happens to have an unusually moderation-heavy day.
    private const double MalformedLineRatioThreshold = 0.5;

    private long? _lastRequestStartedAtTicks;

    public async Task<ChatLogDayResult> ReadDayAsync(
        string twitchChannelId, DateOnly day, long maxBytes, Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)
    {
        await WaitForRequestSlotAsync(ct);
        _lastRequestStartedAtTicks = Environment.TickCount64;

        var path = $"channelid/{twitchChannelId}/{day.Year}/{day.Month}/{day.Day}?raw";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        // A caller cancellation must propagate as-is (see class comment); only a genuine transport
        // failure — including HttpClient's own header-phase Timeout firing — is reported here.
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Log-Archiv-Anfrage für Kanal {ChannelId}, Tag {Day} fehlgeschlagen.", twitchChannelId, day);
            return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 0, null, 0, 0, 0, null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Kein Log-Tag für Kanal {ChannelId}, Tag {Day} (404, laut T8 der Normalzustand).", twitchChannelId, day);
                return new ChatLogDayResult(ChatLogDayStatus.NoLogDay, 0, null, 0, 0, 0, (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Log-Archiv hat Kanal {ChannelId}, Tag {Day} gedrosselt (429), Body wird nicht gelesen.", twitchChannelId, day);
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} lieferte unerwarteten Status {Status}.",
                    twitchChannelId, day, response.StatusCode);
                return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 0, null, 0, 0, 0, (int)response.StatusCode);
            }

            return await ReadBodyAsync(response, twitchChannelId, day, maxBytes, onMessage, ct);
        }
    }

    private async Task<ChatLogDayResult> ReadBodyAsync(
        HttpResponseMessage response, string twitchChannelId, DateOnly day, long maxBytes,
        Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)
    {
        using var bodyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeoutCts.CancelAfter(options.BodyTimeout);
        var bodyCt = bodyTimeoutCts.Token;

        var httpStatusCode = (int)response.StatusCode;
        var messageCount = 0;
        var nonPrivmsgLines = 0;
        var malformedLines = 0;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            var rawStream = await response.Content.ReadAsStreamAsync(bodyCt);
            // Bytes are counted and fed into the digest as they arrive off the wire, before the
            // corresponding text is decoded into a line and handed to the parser — the digest
            // therefore belongs to the received body, not to what the parser made of it.
            await using var countingStream = new CountingHashStream(rawStream, hash);
            using var reader = new StreamReader(countingStream, Encoding.UTF8);

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(bodyCt);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} wegen Body-Timeout ({Timeout}) abgebrochen.",
                        twitchChannelId, day, options.BodyTimeout);
                    return new ChatLogDayResult(
                        ChatLogDayStatus.BodyTimeout, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
                }

                if (line is null)
                {
                    break;
                }

                if (countingStream.BytesRead > maxBytes)
                {
                    logger.LogWarning(
                        "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} über die Byte-Obergrenze ({MaxBytes}) hinaus abgebrochen, Antwort verworfen.",
                        twitchChannelId, day, maxBytes);
                    return new ChatLogDayResult(
                        ChatLogDayStatus.ByteCapExceeded, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
                }

                if (JustlogRawLineParser.TryParse(line, out var message, out var ircCommand))
                {
                    messageCount++;
                    await onMessage(message);
                }
                else if (ircCommand is not null)
                {
                    nonPrivmsgLines++;
                }
                else
                {
                    malformedLines++;
                }
            }

            var totalLines = messageCount + nonPrivmsgLines + malformedLines;
            if (totalLines > 0 && malformedLines / (double)totalLines > MalformedLineRatioThreshold)
            {
                logger.LogWarning(
                    "Log-Archiv-Antwort für Kanal {ChannelId}, Tag {Day} überwiegend nicht als IRC-Zeilen lesbar ({Malformed}/{Total}) — Wurzelform vermutlich geändert.",
                    twitchChannelId, day, malformedLines, totalLines);
                return new ChatLogDayResult(
                    ChatLogDayStatus.MalformedResponse, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            logger.LogInformation(
                "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} abgeschlossen: {Messages} Nachrichten, {Bytes} Bytes.",
                twitchChannelId, day, messageCount, countingStream.BytesRead);
            return new ChatLogDayResult(
                ChatLogDayStatus.Complete, countingStream.BytesRead, digest, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Log-Archiv-Übertragung für Kanal {ChannelId}, Tag {Day} mitten im Body abgebrochen.", twitchChannelId, day);
            return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 0, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken ct)
    {
        if (_lastRequestStartedAtTicks is null)
        {
            return;
        }

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastRequestStartedAtTicks.Value);
        var remaining = options.RequestDelay - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct);
        }
    }

    // Wraps the raw response stream to count bytes and feed them into the running SHA-256 exactly
    // as they are read off the wire, independent of however StreamReader chooses to buffer them —
    // so the digest and byte count reflect the received body, not an approximation reconstructed
    // from decoded text.
    private sealed class CountingHashStream(Stream inner, IncrementalHash hash) : Stream
    {
        public long BytesRead { get; private set; }

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
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (read > 0)
            {
                hash.AppendData(buffer, offset, read);
                BytesRead += read;
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                hash.AppendData(buffer.Span[..read]);
                BytesRead += read;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
