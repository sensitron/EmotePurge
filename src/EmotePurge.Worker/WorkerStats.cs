namespace EmotePurge.Worker;

/// <summary>
/// The worker's own flush bookkeeping, lifted out of <see cref="UsageFlushWorker"/> so the health
/// snapshot can report it. Worker-internal state with no external dependency, so it stays a concrete
/// singleton rather than getting a Core interface — same call as
/// <see cref="SevenTv.SevenTvSubscriptionRegistry"/>, which two hosted services likewise share by
/// concrete type.
/// <para>
/// Clock-free by design (the <c>ReconnectPolicy</c> precedent): the caller passes the timestamp in,
/// which keeps the class deterministically unit-testable without a clock abstraction.
/// </para>
/// </summary>
public sealed class WorkerStats
{
    // One lock rather than Interlocked per field: a success writes three fields that are read
    // together by the health publisher, and a torn read there would show a fresh timestamp next to
    // a stale row count.
    private readonly Lock _lock = new();

    private int _consecutiveFlushFailures;
    private DateTime? _lastFlushSuccessUtc;
    private int? _lastFlushRowCount;

    // Interlocked rather than the lock above: this is a per-message hot-path counter (one increment
    // per chat message with an indeterminate room, called from TwitchChatManager.OnMessageReceived),
    // a much higher frequency than the three flush fields, which are read/written together only
    // once per 30s flush. A single long needs no torn-read protection of its own.
    private long _indeterminateSharedChatMessages;

    // Same pattern as the counter above: one increment per matched line in
    // TwitchChatManager.OnMessageReceived (issue #114), no lock needed for a single long.
    private long _splicedIrcLines;

    public int ConsecutiveFlushFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFlushFailures;
            }
        }
    }

    public DateTime? LastFlushSuccessUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastFlushSuccessUtc;
            }
        }
    }

    public int? LastFlushRowCount
    {
        get
        {
            lock (_lock)
            {
                return _lastFlushRowCount;
            }
        }
    }

    /// <param name="rows">Number of emote counters written by the flush that just succeeded.</param>
    public void RecordFlushSuccess(int rows, DateTime nowUtc)
    {
        lock (_lock)
        {
            _consecutiveFlushFailures = 0;
            _lastFlushSuccessUtc = nowUtc;
            _lastFlushRowCount = rows;
        }
    }

    /// <summary>
    /// Returns the resulting streak length, so the caller can branch on it without a second read
    /// that another thread could have moved in between.
    /// </summary>
    public int RecordFlushFailure()
    {
        lock (_lock)
        {
            return ++_consecutiveFlushFailures;
        }
    }

    /// <summary>
    /// Called once per chat message whose room could not be determined (<c>MessageOrigin.Indeterminate</c>,
    /// #73). No logging here — a log line per message on this hot path would be its own incident;
    /// <see cref="UsageFlushWorker"/> logs the accumulated count once per flush instead.
    /// </summary>
    public void RecordIndeterminateSharedChatMessage() => Interlocked.Increment(ref _indeterminateSharedChatMessages);

    /// <summary>
    /// Reads and zeroes the count in one step, so two overlapping callers can never double-count or
    /// drop the difference between them.
    /// </summary>
    public long TakeIndeterminateSharedChatMessagesSinceLastFlush() =>
        Interlocked.Exchange(ref _indeterminateSharedChatMessages, 0);

    /// <summary>
    /// Called once per IRC line that <see cref="IrcLineSpliceRule.IsSpliced"/> flags as spliced
    /// (#114). No logging here — <see cref="TwitchChatManager"/> already warns once per line with
    /// the tag block; this counter only feeds the once-per-flush summary in
    /// <see cref="UsageFlushWorker"/>.
    /// </summary>
    public void RecordSplicedIrcLine() => Interlocked.Increment(ref _splicedIrcLines);

    /// <summary>
    /// Reads and zeroes the count in one step, so two overlapping callers can never double-count or
    /// drop the difference between them.
    /// </summary>
    public long TakeSplicedIrcLinesSinceLastFlush() =>
        Interlocked.Exchange(ref _splicedIrcLines, 0);
}
