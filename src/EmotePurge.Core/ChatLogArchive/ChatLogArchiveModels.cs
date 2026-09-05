namespace EmotePurge.Core.ChatLogArchive;

/// <summary>
/// One PRIVMSG line from the archive, already IRCv3-tag-decoded and ACTION-unwrapped the same way
/// the live path's TwitchLib <c>ChatMessage</c> presents it to <c>TwitchChatManager</c>.
/// <paramref name="UserId"/> and the entries of <paramref name="Badges"/> are nullable/possibly
/// empty on purpose: the archive can omit both without that being a client error (see
/// <c>JustlogRawLineParser</c>'s class comment) — deciding what an absence means is the harness's
/// job (design doc, "Rückfall ohne Badges"), not this client's.
/// </summary>
public record ChatLogMessage(
    DateTime SentAtUtc,
    string? UserId,
    IReadOnlyList<KeyValuePair<string, string>> Badges,
    string? RoomId,
    string? SourceRoomId,
    string Text);

/// <summary>Outcome of one <see cref="IChatLogArchiveClient.ReadDayAsync"/> call.</summary>
public enum ChatLogDayStatus
{
    /// <summary>The full day was read; <see cref="ChatLogDayResult.BodySha256Hex"/> is set.</summary>
    Complete,

    /// <summary>404 — no log exists for this channel-day. The normal case for most days (T8).</summary>
    NoLogDay,

    /// <summary>429 — the archive throttled this request. The body was never read.</summary>
    RateLimited,

    /// <summary>
    /// The body stalled for longer than the configured body timeout — a separate concern from
    /// <c>HttpClient.Timeout</c>, which only covers the header phase once
    /// <c>HttpCompletionOption.ResponseHeadersRead</c> is used.
    /// </summary>
    BodyTimeout,

    /// <summary>The <c>maxBytes</c> cap was exceeded; the response was discarded mid-stream.</summary>
    ByteCapExceeded,

    /// <summary>A non-2xx status other than 404/429, or a transport error mid-request or mid-body.</summary>
    TransportFailure,

    /// <summary>
    /// More than half of the body's lines failed to parse as any recognized IRC line — the wire
    /// format has likely changed underneath this client (Failure Mode "Wurzelform anders als
    /// angenommen"), not that the channel happens to have an unusually moderation-heavy day.
    /// </summary>
    MalformedResponse
}

/// <summary>
/// Result of reading one channel-day. <paramref name="BodySha256Hex"/> is only populated for
/// <see cref="ChatLogDayStatus.Complete"/> — every other status means the body was partial or
/// never read, so a digest of it would be meaningless.
/// </summary>
public record ChatLogDayResult(
    ChatLogDayStatus Status,
    long BytesReceived,
    string? BodySha256Hex,
    int MessageCount,
    int NonPrivmsgLines,
    int MalformedLines,
    int? HttpStatusCode);
