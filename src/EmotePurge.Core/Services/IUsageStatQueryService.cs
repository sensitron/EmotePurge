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
/// an absent maximum is the honest answer rather than a missing one. A row with <c>UseCount = 0</c>
/// (bot-only usage) does not count as "used" for this field either — the flush can now write such a
/// row, and it is not a day this emote was used.
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

public record EmoteDailyUsageDto(DateOnly Date, int UseCount);

/// <summary>
/// One emote's day-by-day usage series for the drilldown (idea A5).
/// </summary>
/// <param name="Days">
/// Only days with actual usage, ascending. A missing day inside [From, To] means 0 — the flush
/// only ever writes rows for days with real usage, and inventing zero rows server-side just to
/// transport them would be the expensive way to say nothing; the client zero-fills for rendering.
/// A day whose row has <c>UseCount = 0</c> (bot-only usage) is missing here too, for the same
/// reason.
/// </param>
/// <param name="TotalUseCount">Sum over [From, To].</param>
/// <param name="FirstUsedDate">
/// First use ever, deliberately not bounded by the range — same reasoning as
/// <see cref="EmoteUsageContextDto.LastUsedDate"/>. <c>null</c> = never used since tracking began,
/// and a <c>UseCount = 0</c> (bot-only) row does not count as a use.
/// </param>
/// <param name="LastUsedDate">Last use ever, equally unbounded and equally blind to bot-only rows.</param>
/// <param name="LiveDays">
/// Days within [From, To] on which the channel was live (any coverage at all), ascending — so the
/// chart can tell "offline day" apart from "dead emote" (idea A10). Coverage data only exists
/// since the worker's live poll shipped: an absent day before that means "unknown", not
/// "offline", and the consumer must render it unmarked rather than as a statement.
/// </param>
public record EmoteUsageSeriesDto(
    string EmoteId,
    string EmoteName,
    DateOnly From,
    DateOnly To,
    int TotalUseCount,
    DateOnly? FirstUsedDate,
    DateOnly? LastUsedDate,
    IReadOnlyList<EmoteDailyUsageDto> Days,
    IReadOnlyList<DateOnly> LiveDays);

/// <summary>
/// One emote's day rows inside <see cref="ChannelUsageSeriesDto"/>.
/// </summary>
/// <param name="Days">
/// One <c>[dayOffset, useCount]</c> pair per day with actual usage, ascending, where the offset
/// counts days from the range's <c>From</c>. Sparse for the same reason the single-emote series is
/// sparse, and offset-encoded rather than ISO-dated because this is the batch: a channel-wide
/// response carries thousands of these, an ISO date costs about five times what an offset does, and
/// nothing between the Api and the browser compresses JSON (the reverse proxy's gzip covers
/// text/html only). Pairs rather than two parallel arrays so the two halves cannot desynchronize.
/// </param>
public record EmoteSeriesEntryDto(string EmoteId, IReadOnlyList<int[]> Days);

/// <summary>
/// Every active emote's daily usage for one channel and range in a single response — the batch twin
/// of <see cref="EmoteUsageSeriesDto"/>.
/// </summary>
/// <remarks>
/// Exists because the consumer is a hover readout over a sheet of up to a thousand emotes. Asking
/// <see cref="IUsageStatQueryService.GetDailySeriesAsync"/> per emote would turn pointer movement
/// into requests against the endpoint group's rate limiter, and every one of those requests pays
/// for the access filter's two uncached 7TV lookups. One call per (channel, range) removes the
/// question instead of budgeting for it.
/// </remarks>
/// <param name="LiveDays">
/// Day offsets from <paramref name="From"/> on which the channel was live — channel-level, so
/// carried once here rather than repeated per emote as the single-emote series has to. Same
/// "absent means unknown, not offline" caveat as <see cref="EmoteUsageSeriesDto.LiveDays"/>.
/// </param>
/// <param name="Emotes">
/// Only emotes with at least one day of <em>human</em> usage in the range (rows with
/// <c>UseCount = 0</c>, bot-only usage, do not count), and only unarchived ones. An emote the
/// caller knows about but does not find here has no (human) usage in the window — the same
/// statement the omitted days inside an entry make, one level up.
/// </param>
public record ChannelUsageSeriesDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<int> LiveDays,
    IReadOnlyList<EmoteSeriesEntryDto> Emotes);

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
    /// The daily usage series of a single emote, resolved against the channel — <c>null</c> when
    /// the emote id is unknown or belongs to a different channel (the caller answers 404). Archived
    /// emotes deliberately keep their series: they are unreachable from the usage grid, but a
    /// subset vote session still lists them as ballot members, and their history is real.
    /// </summary>
    Task<EmoteUsageSeriesDto?> GetDailySeriesAsync(
        string channelName, string emoteId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every unarchived emote's daily usage for the channel and range at once. An unknown channel
    /// answers with empty lists rather than <c>null</c>: unlike the single-emote series there is no
    /// id to get wrong, and the caller's access filter has already decided whether the channel may
    /// be looked at.
    /// </summary>
    Task<ChannelUsageSeriesDto> GetChannelSeriesAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Range totals for a known set of emote ids, keyed by id and omitting the ones without usage.
    /// Scoped to the caller's ids rather than to a whole channel, because the one caller (a vote
    /// session's ballot) may hold twenty emotes out of a thousand.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
