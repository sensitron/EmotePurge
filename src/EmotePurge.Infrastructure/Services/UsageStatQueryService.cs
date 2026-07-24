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

        // GroupBy+Sum fails to translate when the filtered source still carries the
        // Emote/Channel navigation joins from the Where clause (EF Core/Npgsql limitation:
        // falls back to client-eval "g.AsQueryable().Sum(...)" and throws). Resolving the
        // channel's emote IDs into a plain list first keeps the grouped query scoped to a
        // single table, which translates cleanly.
        var channelEmotes = await db.Emotes
            .Where(e => e.Channel.ChannelName == normalized)
            .Select(e => new { e.Id, e.Name })
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken);

        if (channelEmotes.Count == 0)
        {
            return [];
        }

        var emoteIds = channelEmotes.Keys.ToList();

        var totals = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .GroupBy(u => u.EmoteId)
            .Select(g => new { EmoteId = g.Key, TotalUseCount = g.Sum(u => u.UseCount) })
            .ToListAsync(cancellationToken);

        return totals
            .Select(t => new EmoteUsageTotalDto(t.EmoteId, channelEmotes[t.EmoteId], t.TotalUseCount))
            .OrderByDescending(t => t.TotalUseCount)
            .ToList();
    }
}
