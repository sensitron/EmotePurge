using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatQueryService(AppDbContext db) : IUsageStatQueryService
{
    public async Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();

        return await db.UsageStats
            .Where(u => u.Emote.Channel.ChannelName == normalized)
            .OrderByDescending(u => u.Date).ThenByDescending(u => u.UseCount)
            .Select(u => new EmoteUsageDto(u.Emote.Name, u.Date, u.UseCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmoteUsageTotalDto>> GetUsageTotalsAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException("'from' must be less than or equal to 'to'.", nameof(from));
        }

        var normalized = channelName.Trim().ToLowerInvariant();

        return await db.UsageStats
            .Where(u => u.Emote.Channel.ChannelName == normalized && u.Date >= from && u.Date <= to)
            .GroupBy(u => new { u.EmoteId, u.Emote.Name })
            .Select(g => new EmoteUsageTotalDto(g.Key.EmoteId, g.Key.Name, g.Sum(u => u.UseCount)))
            .OrderByDescending(t => t.TotalUseCount)
            .ToListAsync(cancellationToken);
    }
}
