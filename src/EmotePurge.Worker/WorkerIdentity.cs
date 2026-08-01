namespace EmotePurge.Worker;

/// <summary>
/// Who wrote a snapshot and since when this process has been running. Both exist for one reading
/// problem: a roster that says "0 of 34 channels confirmed" means something entirely different
/// thirty seconds after a deploy than it does after six hours of uptime, and the snapshot itself is
/// the only place an admin can learn which of the two they are looking at. The instance id adds the
/// second question a future multi-replica setup would raise — <em>which</em> worker is this — while
/// costing nothing today.
///
/// Deliberately a plain singleton class rather than an interface (Regel 5): no external dependency,
/// nothing to swap out.
/// </summary>
public sealed class WorkerIdentity
{
    // Short on purpose: this is a label in a log line and on an admin card, not a correlation key.
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    public DateTime ProcessStartedUtc { get; } = DateTime.UtcNow;
}
