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
public record WorkerHealthSnapshot(bool IsConnected, DateTime? LastMessageReceivedUtc, DateTime? ConnectAttemptedUtc);

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
