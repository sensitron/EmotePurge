using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetStatusService(AppDbContext db) : IEmoteSetStatusService
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
        // exist yet would put a query behind every one of those polls for two guaranteed nulls —
        // occupiedSlots and botsExcludedSince share this one gate, not two copies of it.
        int occupiedSlots;
        DateOnly? botsExcludedSince;
        if (channel.ActiveEmoteSetId.Length == 0)
        {
            occupiedSlots = 0;
            botsExcludedSince = null;
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
        }

        return new EmoteSetStatusDto(
            channel.ActiveEmoteSetId,
            channel.ActiveEmoteSetCapacity,
            occupiedSlots,
            channel.TrackingResumedAt ?? channel.CreatedAt,
            channel.LastSyncFailureReason,
            channel.LastSyncAttemptAtUtc,
            botsExcludedSince);
    }
}
