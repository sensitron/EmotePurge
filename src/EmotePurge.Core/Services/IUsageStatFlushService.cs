namespace EmotePurge.Core.Services;

public interface IUsageStatFlushService
{
    /// <summary>
    /// Upserts a drained snapshot of in-memory emote usage counts (Emote.Id → count)
    /// into today's UTC UsageStat rows.
    /// </summary>
    Task FlushAsync(IReadOnlyDictionary<string, int> usageCounts, CancellationToken cancellationToken = default);
}
