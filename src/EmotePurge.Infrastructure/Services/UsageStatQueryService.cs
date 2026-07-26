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
        // Archived (already-deleted) emotes are excluded — they shouldn't reappear as delete
        // candidates in a usage-stats UI just because they still have historical UsageStat rows.
        var channelEmotes = await db.Emotes
            .Where(e => e.Channel.ChannelName == normalized && !e.IsArchived)
            .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl })
            .ToListAsync(cancellationToken);

        if (channelEmotes.Count == 0)
        {
            return [];
        }

        var emoteIds = channelEmotes.Select(e => e.Id).ToList();

        var totalsByEmoteId = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .GroupBy(u => u.EmoteId)
            .Select(g => new { EmoteId = g.Key, TotalUseCount = g.Sum(u => u.UseCount) })
            .ToDictionaryAsync(g => g.EmoteId, g => g.TotalUseCount, cancellationToken);

        // Zero-filled for every active emote (not just ones with a UsageStat row already) —
        // an unused-but-active emote must still be findable/selectable in a usage-stats UI.
        return channelEmotes
            .Select(e => new EmoteUsageTotalDto(e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, totalsByEmoteId.GetValueOrDefault(e.Id, 0)))
            .OrderByDescending(t => t.TotalUseCount)
            .ToList();
    }
}
