namespace EmotePurge.Core.Services;

/// <summary>
/// One channel as the worker currently sees it, joining the two transports that have to agree for a
/// channel to actually be tracked: Twitch IRC (do we receive chat at all) and the 7TV EventAPI (do
/// we hear about emote-set changes). A channel present in one and absent from the other is the
/// failure this snapshot exists to make visible.
/// </summary>
/// <param name="LastMessageUtc">
/// Last chat message in this channel since the worker started. Null on a quiet channel is normal —
/// read it together with <paramref name="IrcJoinConfirmed"/>, never on its own.
/// </param>
/// <param name="SevenTvEmoteSetId">
/// Null means the worker holds no 7TV subscription intent for this channel at all, which after a
/// successful sync should not happen and is therefore worth showing.
/// </param>
/// <param name="SevenTvUserId">
/// Null means no user subscription is desired (the channel's 7TV user id was never resolved), so
/// <paramref name="SevenTvUserAcknowledged"/> being false is a statement about nothing, not a
/// pending acknowledgement.
/// </param>
public record WorkerRosterChannel(
    string ChannelName,
    bool IrcJoinConfirmed,
    DateTime? LastMessageUtc,
    string? SevenTvEmoteSetId,
    bool SevenTvEmoteSetAcknowledged,
    string? SevenTvUserId,
    bool SevenTvUserAcknowledged);

/// <summary>
/// The worker's per-channel roster, published to Redis on its own key rather than folded into the
/// health snapshot: the health key is read on every worker.health event (~20s) by every open
/// monitoring page, and the roster grows with the channel count.
/// </summary>
/// <param name="GeneratedAtUtc">
/// How staleness is signalled. The key's TTL only says "gone"; a reader needs to know that a
/// present snapshot is two minutes old, so the age travels in the payload and the API derives it.
/// </param>
/// <param name="BootRecoveryCompleted">
/// False means the worker is still rejoining after a restart, and every "missing" channel below is
/// expected rather than broken. Without this a redeploy makes the roster cry wolf every single time.
/// </param>
/// <param name="Truncated">
/// True when the worker capped the list. Stated rather than silently dropped: a capped list that
/// looks complete is worse than no list.
/// </param>
public record WorkerRosterSnapshot(
    DateTime GeneratedAtUtc,
    string WorkerInstanceId,
    DateTime ProcessStartedUtc,
    bool BootRecoveryCompleted,
    bool Truncated,
    IReadOnlyList<WorkerRosterChannel>? Channels = null)
{
    /// <summary>
    /// Never null, whatever the payload said. A snapshot written by a worker that predates a future
    /// rename — or one whose array was dropped — must read as "no rows" in one place here instead of
    /// as a null check in every consumer.
    /// </summary>
    public IReadOnlyList<WorkerRosterChannel> Channels { get; init; } = Channels ?? [];
}

/// <summary>
/// Reads the roster snapshot the worker publishes. Same arrangement as
/// <see cref="IWorkerHealthReader"/>: API and worker never talk directly, and the wire format has a
/// single home.
/// </summary>
public interface IWorkerRosterReader
{
    /// <summary>
    /// <c>null</c> when the key is absent (expired, or the worker never started) or unreadable —
    /// which is itself the answer, not an error.
    /// </summary>
    Task<WorkerRosterSnapshot?> ReadAsync(CancellationToken cancellationToken = default);
}
