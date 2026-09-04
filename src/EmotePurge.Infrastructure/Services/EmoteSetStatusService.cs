using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetStatusService(
    AppDbContext db,
    IDuplicateEmoteNameQueryService duplicateEmoteNameQueryService) : IEmoteSetStatusService
{
    public async Task<EmoteSetStatusDto?> GetAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        // Skipped entirely while no set is known: that is exactly the window the usage-stats page
        // polls this endpoint in a loop waiting for the first sync, and counting rows that cannot
        // exist yet would put a query behind every one of those polls for three guaranteed empties —
        // occupiedSlots, botsExcludedSince and the collision list share this one gate, not three
        // copies of it.
        int occupiedSlots;
        DateOnly? botsExcludedSince;
        IReadOnlyList<DuplicateEmoteNameDto> duplicateNames;
        if (channel.ActiveEmoteSetId.Length == 0)
        {
            occupiedSlots = 0;
            botsExcludedSince = null;
            duplicateNames = [];
        }
        else
        {
            occupiedSlots = await db.Emotes.CountAsync(e => e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);

            // Rule 10: resolve the channel's emote ids to a plain scalar list first, then
            // aggregate over UsageStats alone — the same shape GetUsageContextAsync uses, for the
            // same reason (a MIN grouped straight off a Where that still carries the Emote
            // navigation risks the client-eval fallback that GroupBy hits there). Archived emotes
            // are deliberately included: a bot sighting on an emote since deleted from 7TV still
            // tells us when the separation started for this channel. Projected to DateOnly? — a
            // non-nullable Min throws on an empty result set, and "no bot ever seen" is exactly
            // the empty case this has to handle without an exception.
            var emoteIds = await db.Emotes
                .Where(e => e.ChannelId == channel.Id)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken);

            botsExcludedSince = await db.UsageStats
                .Where(u => emoteIds.Contains(u.EmoteId) && u.BotUseCount > 0)
                .Select(u => (DateOnly?)u.Date)
                .MinAsync(cancellationToken);

            // Delegated rather than reimplemented from the rows already loaded above: the grouping
            // has to be ordinal case-sensitive to mirror chat matching, and a second copy of that
            // rule is exactly the kind that drifts. The cost is one extra indexed channel lookup
            // inside the query service — cheap next to the round trip this field exists to save.
            // `?? []` is unreachable in practice (the channel was found a moment ago) and means the
            // same thing either way: nothing to report.
            duplicateNames = await duplicateEmoteNameQueryService.GetAsync(channelName, cancellationToken) ?? [];
        }

        return new EmoteSetStatusDto(
            channel.ActiveEmoteSetId,
            channel.ActiveEmoteSetCapacity,
            occupiedSlots,
            channel.TrackingResumedAt ?? channel.CreatedAt,
            channel.LastSyncFailureReason,
            channel.LastSyncAttemptAtUtc,
            botsExcludedSince,
            duplicateNames);
    }
}
