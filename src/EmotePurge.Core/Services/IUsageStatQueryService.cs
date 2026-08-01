namespace EmotePurge.Core.Services;

public record EmoteUsageDto(string EmoteName, DateOnly Date, int UseCount);

/// <summary>
/// One emote with everything needed to judge it as a deletion candidate.
/// </summary>
/// <param name="TotalUseCount">Sum over the requested range.</param>
/// <param name="LastUsedDate">
/// The last day this emote was used at all — deliberately <em>not</em> bounded by the requested
/// range. Bounded, it would collapse into the total ("0 uses in the range" already says that) and
/// switching the range to 7 days would report almost the whole set as never used. <c>null</c> means
/// never used since tracking began: the flush only ever writes rows for days with actual usage, so
/// an absent maximum is the honest answer rather than a missing one.
/// </param>
/// <param name="PreviousWindowUseCount">
/// Sum over the equally long window immediately preceding the requested range (<c>from</c>
/// exclusive). Deliberately a raw number: whether that reads as rising, stable or falling is a
/// wording decision, and one the caller has to be able to suppress when the history is too short to
/// support it.
/// </param>
/// <param name="FirstSeenAt">
/// When the emote entered the 7TV set. <c>null</c> means unknown — never "new".
/// </param>
public record EmoteUsageContextDto(
    string EmoteId,
    string EmoteName,
    string SevenTvEmoteId,
    string ImageUrl,
    int TotalUseCount,
    DateOnly? LastUsedDate,
    int PreviousWindowUseCount,
    DateTime? FirstSeenAt);

public interface IUsageStatQueryService
{
    Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every active emote of the channel with its usage context, zero-filled — an unused emote must
    /// still be findable in a usage UI. Archived emotes are excluded: they are already gone from
    /// 7TV and must not reappear as deletion candidates just because they still carry history.
    /// </summary>
    Task<IReadOnlyList<EmoteUsageContextDto>> GetUsageContextAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Range totals for a known set of emote ids, keyed by id and omitting the ones without usage.
    /// Scoped to the caller's ids rather than to a whole channel, because the one caller (a vote
    /// session's ballot) may hold twenty emotes out of a thousand.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
