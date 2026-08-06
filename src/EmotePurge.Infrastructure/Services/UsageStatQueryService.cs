using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatQueryService(AppDbContext db) : IUsageStatQueryService
{
    public async Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        return await db.UsageStats
            .Where(u => u.Emote.Channel.ChannelName == normalized)
            .OrderByDescending(u => u.Date).ThenByDescending(u => u.UseCount)
            .Select(u => new EmoteUsageDto(u.Emote.Name, u.Date, u.UseCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmoteUsageContextDto>> GetUsageContextAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException("'from' must be less than or equal to 'to'.", nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        // GroupBy+Sum fails to translate when the filtered source still carries the
        // Emote/Channel navigation joins from the Where clause (EF Core/Npgsql limitation:
        // falls back to client-eval "g.AsQueryable().Sum(...)" and throws). Resolving the
        // channel's emote IDs into a plain list first keeps the grouped query scoped to a
        // single table, which translates cleanly.
        // Archived (already-deleted) emotes are excluded — they shouldn't reappear as delete
        // candidates in a usage-stats UI just because they still have historical UsageStat rows.
        var channelEmotes = await db.Emotes
            .Where(e => e.Channel.ChannelName == normalized && !e.IsArchived)
            .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.FirstSeenAt })
            .ToListAsync(cancellationToken);

        if (channelEmotes.Count == 0)
        {
            return [];
        }

        var emoteIds = channelEmotes.Select(e => e.Id).ToList();

        // Both range bounds are inclusive, so the window is one day longer than the difference —
        // and the preceding window has to be exactly as long for the two sums to be comparable.
        var windowLength = to.DayNumber - from.DayNumber + 1;
        var previousFrom = from.AddDays(-windowLength);

        // One pass, three aggregates, all served by the covering index (EmoteId, Date) INCLUDE
        // (UseCount) as an index-only scan. Deliberately unbounded in time: the max is the emote's
        // last use ever, and clipping it to the range would make it a restatement of the total.
        var aggregates = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId))
            .GroupBy(u => u.EmoteId)
            .Select(g => new
            {
                EmoteId = g.Key,
                TotalUseCount = g.Sum(u => u.Date >= from && u.Date <= to ? u.UseCount : 0),
                PreviousWindowUseCount = g.Sum(u => u.Date >= previousFrom && u.Date < from ? u.UseCount : 0),
                LastUsedDate = g.Max(u => (DateOnly?)u.Date)
            })
            .ToDictionaryAsync(g => g.EmoteId, cancellationToken);

        // Zero-filled for every active emote (not just ones with a UsageStat row already) —
        // an unused-but-active emote must still be findable/selectable in a usage-stats UI.
        return channelEmotes
            .Select(e =>
            {
                var aggregate = aggregates.GetValueOrDefault(e.Id);
                return new EmoteUsageContextDto(
                    e.Id,
                    e.Name,
                    e.SevenTvEmoteId,
                    e.ImageUrl,
                    aggregate?.TotalUseCount ?? 0,
                    aggregate?.LastUsedDate,
                    aggregate?.PreviousWindowUseCount ?? 0,
                    e.FirstSeenAt);
            })
            .OrderByDescending(t => t.TotalUseCount)
            .ToList();
    }

    public async Task<EmoteUsageSeriesDto?> GetDailySeriesAsync(
        string channelName, string emoteId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException("'from' must be less than or equal to 'to'.", nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        // Resolved against the channel, not looked up by id alone: emoteId is a client-supplied
        // value, and without the join a caller with access to channel A could read the series of an
        // emote from channel B. IsArchived is deliberately not filtered — an archived emote is
        // unreachable from the usage grid, but a subset vote session still lists it as a ballot
        // member, and its history is real.
        var emote = await db.Emotes
            .Where(e => e.Id == emoteId && e.Channel.ChannelName == normalized)
            .Select(e => new { e.Id, e.Name, e.ChannelId })
            .FirstOrDefaultAsync(cancellationToken);
        if (emote is null)
        {
            return null;
        }

        // Sparse on purpose (only days with usage) — served by the covering index
        // (EmoteId, Date) INCLUDE (UseCount) as an index-only scan.
        var days = await db.UsageStats
            .Where(u => u.EmoteId == emote.Id && u.Date >= from && u.Date <= to)
            .OrderBy(u => u.Date)
            .Select(u => new EmoteDailyUsageDto(u.Date, u.UseCount))
            .ToListAsync(cancellationToken);

        // First/last use ever, unbounded in time — same reasoning as LastUsedDate in
        // GetUsageContextAsync. Single-table GroupBy, so rule 10 is not even touched.
        var bounds = await db.UsageStats
            .Where(u => u.EmoteId == emote.Id)
            .GroupBy(u => u.EmoteId)
            .Select(g => new
            {
                First = g.Min(u => (DateOnly?)u.Date),
                Last = g.Max(u => (DateOnly?)u.Date)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Range-bounded unlike the bounds above: the consumer overlays these on exactly the
        // rendered window. LiveMinutes > 0 is defensive — the poll never writes a zero row.
        // Served by the covering index (ChannelId, Date) INCLUDE (LiveMinutes).
        var liveDays = await db.ChannelLiveDays
            .Where(l => l.ChannelId == emote.ChannelId && l.Date >= from && l.Date <= to && l.LiveMinutes > 0)
            .OrderBy(l => l.Date)
            .Select(l => l.Date)
            .ToListAsync(cancellationToken);

        return new EmoteUsageSeriesDto(
            emote.Id,
            emote.Name,
            from,
            to,
            days.Sum(d => d.UseCount),
            bounds?.First,
            bounds?.Last,
            days,
            liveDays);
    }

    public async Task<ChannelUsageSeriesDto> GetChannelSeriesAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException("'from' must be less than or equal to 'to'.", nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        var channelId = await db.Channels
            .Where(c => c.ChannelName == normalized)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (channelId is null)
        {
            return new ChannelUsageSeriesDto(from, to, [], []);
        }

        // Same exclusion as GetUsageContextAsync: this feeds the usage grid, and an archived emote
        // is not on it. The single-emote series keeps archived emotes because a ballot can still
        // list them — that caller asks by id and knows what it is asking for.
        var emoteIds = await db.Emotes
            .Where(e => e.ChannelId == channelId && !e.IsArchived)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var liveDays = await db.ChannelLiveDays
            .Where(l => l.ChannelId == channelId && l.Date >= from && l.Date <= to && l.LiveMinutes > 0)
            .OrderBy(l => l.Date)
            .Select(l => l.Date)
            .ToListAsync(cancellationToken);
        var liveDayOffsets = liveDays.Select(d => d.DayNumber - from.DayNumber).ToList();

        if (emoteIds.Count == 0)
        {
            return new ChannelUsageSeriesDto(from, to, liveDayOffsets, []);
        }

        // One index-only scan over (EmoteId, Date) INCLUDE (UseCount) for the whole channel, then
        // grouped in memory. Deliberately not a GroupBy in SQL: the grouping here is pure
        // partitioning with no aggregate to push down, so the database would do the same work and
        // hand back the same number of rows either way — and rule 10 makes a navigation-joined
        // GroupBy the fragile shape to reach for. Ordering by (EmoteId, Date) is what lets the
        // in-memory GroupBy below emit each emote's days already ascending.
        var rows = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .OrderBy(u => u.EmoteId).ThenBy(u => u.Date)
            .Select(u => new { u.EmoteId, u.Date, u.UseCount })
            .ToListAsync(cancellationToken);

        var emotes = rows
            .GroupBy(r => r.EmoteId)
            .Select(g => new EmoteSeriesEntryDto(
                g.Key,
                g.Select(r => new[] { r.Date.DayNumber - from.DayNumber, r.UseCount }).ToList()))
            .ToList();

        return new ChannelUsageSeriesDto(from, to, liveDayOffsets, emotes);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException("'from' must be less than or equal to 'to'.", nameof(from));
        }

        if (emoteIds.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        // Materialized list rather than the caller's collection: the same rule-10 reason as above,
        // the grouped query has to stay scoped to a single table.
        var ids = emoteIds.ToList();

        return await db.UsageStats
            .Where(u => ids.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .GroupBy(u => u.EmoteId)
            .Select(g => new { EmoteId = g.Key, TotalUseCount = g.Sum(u => u.UseCount) })
            .ToDictionaryAsync(g => g.EmoteId, g => g.TotalUseCount, cancellationToken);
    }
}
