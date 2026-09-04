namespace EmotePurge.Core.Services;

/// <summary>
/// The channel's 7TV set as a slot budget: how many slots the set has and how many are taken.
/// </summary>
/// <param name="ActiveEmoteSetId">Empty while the first sync is still pending.</param>
/// <param name="Capacity">
/// The set's slot limit as 7TV last reported it, or <c>null</c> when it did not report one.
/// Consumers must render no budget at all in that case rather than assuming 1000 — 7TV subscribers
/// get larger sets, and inventing a denominator would understate how full the set is.
/// </param>
/// <param name="OccupiedSlots">
/// Counted from our own emote rows, not from 7TV's <c>emote_count</c>. The two agree right after
/// every full sync anyway (the sync reconciles onto exactly that list), but between syncs only our
/// count reflects the EventAPI deltas that have already arrived — and it is the number the user
/// sees in the grid below the bar.
/// </param>
/// <param name="TrackedSince">
/// Since when this channel's usage data can be trusted: the last join that reactivated the channel,
/// or its creation if it was never left and rejoined. Older than this, we simply were not counting.
/// </param>
/// <param name="SyncFailureReason">
/// One of <see cref="SevenTvSyncFailureReasons"/>, or <c>null</c> when the last sync attempt
/// succeeded — or when none has been made yet. Together with an empty
/// <paramref name="ActiveEmoteSetId"/> that absence is what tells "the first sync is still running"
/// apart from "this channel has no active emote set on 7TV", which used to look identical.
/// </param>
/// <param name="LastSyncAttemptAtUtc">
/// When the last attempt finished, successful or not. <c>null</c> means none has been made. Read
/// with <paramref name="SyncFailureReason"/>: it says how current the reason is.
/// </param>
/// <param name="BotsExcludedSince">
/// The earliest UTC day on which this channel has a <c>UsageStat</c> row with <c>BotUseCount &gt;
/// 0</c> — the first time a bot was <em>seen</em> here, not the day bot usage started being
/// counted apart. That separation began the moment this feature was deployed, which is an event
/// in the deploy history, not in the data: rows written before and after look identical when
/// <c>BotUseCount</c> happens to be 0, so there is no way to derive the true cutover from the
/// data alone. For a channel joined long before the deploy, this understates how far back the
/// mixing goes; for one joined after, it can wrongly suggest older numbers are mixed when they
/// never were. <c>null</c> means no bot has ever been seen here, in which case there is nothing
/// to explain and a consumer should show nothing.
/// </param>
public record EmoteSetStatusDto(
    string ActiveEmoteSetId,
    int? Capacity,
    int OccupiedSlots,
    DateTime TrackedSince,
    string? SyncFailureReason,
    DateTime? LastSyncAttemptAtUtc,
    DateOnly? BotsExcludedSince);

public interface IEmoteSetStatusService
{
    /// <summary>Returns <c>null</c> for a channel that is not tracked at all.</summary>
    Task<EmoteSetStatusDto?> GetAsync(string channelName, CancellationToken cancellationToken = default);
}
