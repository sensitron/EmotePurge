using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetStatusService(AppDbContext db, IUsageStatQueryService usageStatQueryService) : IEmoteSetStatusService
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
        // exist yet would put a query behind every one of those polls for guaranteed nulls —
        // occupiedSlots, botsExcludedSince and sharedChatSeparatedSince share this one gate, not
        // three copies of it.
        int occupiedSlots;
        DateOnly? botsExcludedSince;
        DateOnly? sharedChatSeparatedSince;
        if (channel.ActiveEmoteSetId.Length == 0)
        {
            occupiedSlots = 0;
            botsExcludedSince = null;
            sharedChatSeparatedSince = null;
        }
        else
        {
            occupiedSlots = await db.Emotes.CountAsync(e => e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);
            botsExcludedSince = await usageStatQueryService.GetEarliestBotUsageDateAsync(channel.Id, cancellationToken);
            sharedChatSeparatedSince = await usageStatQueryService.GetEarliestSharedChatUsageDateAsync(channel.Id, cancellationToken);
        }

        return new EmoteSetStatusDto(
            channel.ActiveEmoteSetId,
            channel.ActiveEmoteSetCapacity,
            occupiedSlots,
            TrackingCoverage.TrackedSince(channel.TrackingResumedAt, channel.CreatedAt),
            channel.LastSyncFailureReason,
            channel.LastSyncAttemptAtUtc,
            botsExcludedSince,
            sharedChatSeparatedSince);
    }
}
