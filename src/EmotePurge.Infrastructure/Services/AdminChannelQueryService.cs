using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AdminChannelQueryService(AppDbContext db) : IAdminChannelQueryService
{
    public async Task<IReadOnlyList<AdminChannelDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        // The channel table is small by design (tens of rows, JOIN-limited on the Twitch side), so
        // three simple queries plus an in-memory stitch beat one correlated mega-query on both
        // readability and translation risk.
        var channels = await db.Channels
            .OrderBy(c => c.ChannelName)
            .Select(c => new { c.Id, c.ChannelName, c.TwitchChannelId, c.IsBotActive, c.CreatedAt })
            .ToListAsync(cancellationToken);

        if (channels.Count == 0)
        {
            return [];
        }

        var channelIds = channels.Select(c => c.Id).ToList();

        // Regel 10: both aggregates group on the plain FK column of a single table and filter via a
        // scalar ID list. Grouping a source that still carries the Channel navigation join makes
        // EF Core/Npgsql fall back to client evaluation and throw (see UsageStatQueryService).
        var emoteAggregates = await db.Emotes
            .Where(e => channelIds.Contains(e.ChannelId))
            .GroupBy(e => e.ChannelId)
            .Select(g => new
            {
                ChannelId = g.Key,
                EmoteCount = g.Count(),
                ArchivedEmoteCount = g.Count(e => e.IsArchived),
                LastSyncedAtUtc = g.Max(e => (DateTime?)e.LastSyncedAt),
            })
            .ToDictionaryAsync(a => a.ChannelId, cancellationToken);

        var voteSessionAggregates = await db.VoteSessions
            .Where(v => channelIds.Contains(v.ChannelId))
            .GroupBy(v => v.ChannelId)
            .Select(g => new
            {
                ChannelId = g.Key,
                VoteSessionCount = g.Count(),
                ActiveVoteSessionCount = g.Count(v => v.IsActive),
            })
            .ToDictionaryAsync(a => a.ChannelId, cancellationToken);

        // Zero-filled: a channel without emotes or sessions must still appear as a row with zeros,
        // not drop out of the list the way an inner join would make it.
        return channels
            .Select(c =>
            {
                emoteAggregates.TryGetValue(c.Id, out var emotes);
                voteSessionAggregates.TryGetValue(c.Id, out var sessions);

                return new AdminChannelDto(
                    c.ChannelName,
                    c.TwitchChannelId,
                    c.IsBotActive,
                    c.CreatedAt,
                    emotes?.EmoteCount ?? 0,
                    emotes?.ArchivedEmoteCount ?? 0,
                    sessions?.ActiveVoteSessionCount ?? 0,
                    sessions?.VoteSessionCount ?? 0,
                    emotes?.LastSyncedAtUtc);
            })
            .ToList();
    }
}
