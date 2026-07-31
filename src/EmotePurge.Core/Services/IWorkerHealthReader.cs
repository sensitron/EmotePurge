namespace EmotePurge.Core.Services;

/// <summary>
/// The worker's Twitch connection health, as published to Redis. One type shared by writer and
/// reader: the worker used to serialize this as an anonymous object and the API deserialize it into
/// its own private record, so the same wire format was declared twice with nothing tying them
/// together — and the health payload is precisely the place where the next field gets appended.
/// </summary>
/// <param name="ConnectAttemptedUtc">
/// Reference point for staleness while no chat message has ever arrived. Without it a freshly started
/// worker is indistinguishable from one that has been connected but silent for hours.
/// </param>
/// <param name="SevenTvLastFrameUtc">
/// Any received EventAPI frame including heartbeats (~45s cadence) — the liveness signal. The last
/// dispatch is deliberately not used for staleness: a channel whose set nobody edits receives no
/// dispatches for days and would read as permanently stale.
/// </param>
/// <param name="SevenTvLastDispatchUtc">Observability only; never feeds a status derivation.</param>
public record WorkerHealthSnapshot(
    bool IsConnected,
    DateTime? LastMessageReceivedUtc,
    DateTime? ConnectAttemptedUtc,
    // 7TV EventAPI fields, appended 2026-07-30. Defaults equal default(T) so a payload written by
    // an older worker deserializes without special-casing during a rolling deploy.
    bool SevenTvEnabled = false,
    bool SevenTvConnected = false,
    DateTime? SevenTvLastFrameUtc = null,
    DateTime? SevenTvLastDispatchUtc = null,
    DateTime? SevenTvConnectAttemptedUtc = null,
    // Admin-only detail fields, appended 2026-07-31 for GET /api/admin/health (Z1 health split).
    // Nullable rather than 0-defaulted on purpose: a payload from an older worker must read as
    // "unknown", not as "zero subscriptions / zero failures", which would look like a healthy idle
    // worker on the monitoring page. The public GET /api/worker/health never surfaces these.
    int? SevenTvDesiredChannelCount = null,
    int? SevenTvDesiredSubscriptionCount = null,
    int? SevenTvUnacknowledgedCount = null,
    int? FlushConsecutiveFailures = null,
    DateTime? FlushLastSuccessUtc = null,
    int? FlushLastRowCount = null,
    int? PendingEmoteCount = null);

/// <summary>
/// Reads the health snapshot the worker publishes. The API and the worker deliberately never talk
/// directly — this keeps that true while giving the contract a single home. Owns the Redis key, which
/// the API previously repeated as a string literal because the two projects do not reference each
/// other.
/// </summary>
public interface IWorkerHealthReader
{
    /// <summary>
    /// <c>null</c> when the key is absent (expired, or the worker never started) or unreadable. That
    /// absence is itself the signal — the caller reports it, it is not an error.
    /// </summary>
    Task<WorkerHealthSnapshot?> ReadAsync(CancellationToken cancellationToken = default);
}
