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
public record EmoteSetStatusDto(string ActiveEmoteSetId, int? Capacity, int OccupiedSlots, DateTime TrackedSince);

public interface IEmoteSetStatusService
{
    /// <summary>Returns <c>null</c> for a channel that is not tracked at all.</summary>
    Task<EmoteSetStatusDto?> GetAsync(string channelName, CancellationToken cancellationToken = default);
}
