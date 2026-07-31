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
}
