using System.Linq.Expressions;
using EmotePurge.Core.Entities;
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
            .Select(ChannelRow.Projection)
            .ToListAsync(cancellationToken);

        return channels.Count == 0 ? [] : await BuildAsync(channels, cancellationToken);
    }

    public async Task<AdminChannelDto?> GetAsync(string channelName, CancellationToken cancellationToken = default)
    {
        // Regel 9: the lookup filters on the normalized name, like every other channel lookup.
        var normalized = ChannelName.Normalize(channelName);
        var channel = await db.Channels
            .Where(c => c.ChannelName == normalized)
            .Select(ChannelRow.Projection)
            .SingleOrDefaultAsync(cancellationToken);

        if (channel is null)
        {
            return null;
        }

        // Same aggregation path as the list, so the drilldown can never disagree with the row the
        // admin clicked — the one-element list costs a second query on a single-row scan.
        var rows = await BuildAsync([channel], cancellationToken);
        return rows[0];
    }

    public async Task<IReadOnlyList<string>> ListActiveChannelNamesAsync(CancellationToken cancellationToken = default)
        => await db.Channels
            .Where(c => c.IsBotActive)
            .OrderBy(c => c.ChannelName)
            .Select(c => c.ChannelName)
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<AdminChannelDto>> BuildAsync(
        IReadOnlyList<ChannelRow> channels,
        CancellationToken cancellationToken)
    {
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
                LastInventoryChangeUtc = g.Max(e => (DateTime?)e.LastSyncedAt),
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
                    c.LastSyncedAtUtc,
                    emotes?.LastInventoryChangeUtc,
                    // Empty reads as null: the column defaults to "" for a channel that has never
                    // synced, and an empty string in a JSON payload is a worse "unknown" than null.
                    c.ActiveEmoteSetId is { Length: > 0 } setId ? setId : null,
                    c.ActiveEmoteSetCapacity,
                    c.TrackingResumedAt);
            })
            .ToList();
    }

    /// <summary>
    /// The columns both paths read, projected once. A record rather than an anonymous type so the
    /// list and the single-row query can share one expression instead of two that could drift.
    /// </summary>
    private sealed record ChannelRow(
        string Id,
        string ChannelName,
        string? TwitchChannelId,
        bool IsBotActive,
        DateTime CreatedAt,
        DateTime? LastSyncedAtUtc,
        string ActiveEmoteSetId,
        int? ActiveEmoteSetCapacity,
        DateTime? TrackingResumedAt)
    {
        public static Expression<Func<Channel, ChannelRow>> Projection { get; } =
            c => new ChannelRow(
                c.Id,
                c.ChannelName,
                c.TwitchChannelId,
                c.IsBotActive,
                c.CreatedAt,
                c.LastSyncedAtUtc,
                c.ActiveEmoteSetId,
                c.ActiveEmoteSetCapacity,
                c.TrackingResumedAt);
    }
}
