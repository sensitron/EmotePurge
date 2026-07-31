namespace EmotePurge.Core.Services;

public interface IUsageStatFlushService
{
    /// <summary>
    /// Upserts a drained snapshot of in-memory emote usage counts (Emote.Id → count)
    /// into today's UTC UsageStat rows.
    /// </summary>
    /// <returns>
    /// The distinct normalized names of the channels whose emotes were actually written — the input
    /// alone cannot answer that, since counts for meanwhile-deleted emotes are dropped. Empty when
    /// nothing was written. Callers use it to announce the change; it is not an error signal.
    /// </returns>
    Task<IReadOnlyCollection<string>> FlushAsync(IReadOnlyDictionary<string, int> usageCounts, CancellationToken cancellationToken = default);
}
