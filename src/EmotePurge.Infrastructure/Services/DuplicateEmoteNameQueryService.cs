using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class DuplicateEmoteNameQueryService(AppDbContext db) : IDuplicateEmoteNameQueryService
{
    public async Task<IReadOnlyList<DuplicateEmoteNameDto>?> GetAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        var activeEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
            .Select(e => new { e.Id, e.SevenTvEmoteId, e.Name, e.ImageUrl })
            .ToListAsync(cancellationToken);

        // Grouped in memory: the emote details are needed per group anyway, and the grouping must
        // be ordinal case-sensitive to mirror chat matching — casing variants are not collisions.
        return activeEmotes
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DuplicateEmoteNameDto(
                g.Key,
                g.OrderBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
                    .Select(e => new DuplicateEmoteDto(e.Id, e.SevenTvEmoteId, e.ImageUrl))
                    .ToList()))
            .ToList();
    }
}
