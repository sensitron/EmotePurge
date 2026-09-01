namespace EmotePurge.Core.Services;

/// <summary>
/// A pair of usage counts for one emote, split by who triggered it.
/// </summary>
/// <param name="Human">
/// What <c>UsageStat.UseCount</c> has always meant and now means exclusively: chat messages from
/// chatters that <c>IBotChatterDetector</c> did not classify as a bot.
/// </param>
/// <param name="Bot">
/// What lands in <c>UsageStat.BotUseCount</c>: chat messages from a recognized bot account. Kept,
/// not dropped — see the DECISIONS entry for 2026-09-01 on why a misclassification in either
/// direction stays reparable.
/// </param>
public readonly record struct EmoteUsageCounts(int Human, int Bot);

public interface IUsageStatFlushService
{
    /// <summary>
    /// Upserts a drained snapshot of in-memory emote usage counts (Emote.Id → (human, bot))
    /// into today's UTC UsageStat rows.
    /// </summary>
    /// <returns>
    /// The distinct normalized names of the channels whose emotes were actually written — the input
    /// alone cannot answer that, since counts for meanwhile-deleted emotes are dropped. Empty when
    /// nothing was written. Callers use it to announce the change; it is not an error signal.
    /// </returns>
    Task<IReadOnlyCollection<string>> FlushAsync(IReadOnlyDictionary<string, EmoteUsageCounts> usageCounts, CancellationToken cancellationToken = default);
}
