using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class LiveCoverageService(AppDbContext db) : ILiveCoverageService
{
    private const int MinutesPerDay = 1440;

    public async Task<int> AddLiveMinutesAsync(
        IReadOnlyCollection<string> liveChannelNames, DateOnly dateUtc, int minutes, CancellationToken cancellationToken = default)
    {
        if (liveChannelNames.Count == 0 || minutes <= 0)
        {
            return 0;
        }

        var normalized = liveChannelNames.Select(ChannelName.Normalize).Distinct().ToList();

        var channelIds = await db.Channels
            .Where(c => normalized.Contains(c.ChannelName))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (channelIds.Count == 0)
        {
            return 0;
        }

        // Read-modify-write instead of an upsert statement: one worker is the only writer, one
        // call per poll tick, a handful of rows — the simple form wins over ON CONFLICT here.
        var existing = await db.ChannelLiveDays
            .Where(d => d.Date == dateUtc && channelIds.Contains(d.ChannelId))
            .ToDictionaryAsync(d => d.ChannelId, cancellationToken);

        foreach (var channelId in channelIds)
        {
            if (existing.TryGetValue(channelId, out var row))
            {
                row.LiveMinutes = Math.Min(MinutesPerDay, row.LiveMinutes + minutes);
            }
            else
            {
                db.ChannelLiveDays.Add(new ChannelLiveDay
                {
                    ChannelId = channelId,
                    Date = dateUtc,
                    LiveMinutes = Math.Min(MinutesPerDay, minutes)
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return channelIds.Count;
    }
}
