using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatFlushService(AppDbContext db, ILogger<UsageStatFlushService> logger) : IUsageStatFlushService
{
    public async Task FlushAsync(IReadOnlyDictionary<string, int> usageCounts, CancellationToken cancellationToken = default)
    {
        if (usageCounts.Count == 0)
        {
            return;
        }

        var today = DateTime.UtcNow.Date;
        var emoteIds = usageCounts.Keys.ToList();

        // A channel leave hard-deletes its Emote rows (cascade); a count buffered before
        // that leave could otherwise violate the UsageStat.EmoteId FK on flush.
        var validIds = await db.Emotes
            .Where(e => emoteIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToHashSetAsync(cancellationToken);

        if (validIds.Count < emoteIds.Count)
        {
            logger.LogWarning(
                "{Count} gepufferte Usage-Counts für inzwischen gelöschte Emotes verworfen.",
                emoteIds.Count - validIds.Count);
        }

        var existingStats = await db.UsageStats
            .Where(u => u.Date == today && validIds.Contains(u.EmoteId))
            .ToDictionaryAsync(u => u.EmoteId, cancellationToken);

        foreach (var emoteId in validIds)
        {
            if (existingStats.TryGetValue(emoteId, out var stat))
            {
                stat.UseCount += usageCounts[emoteId];
            }
            else
            {
                db.UsageStats.Add(new UsageStat { EmoteId = emoteId, Date = today, UseCount = usageCounts[emoteId] });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
