using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionQueryService(AppDbContext db, IUsageStatQueryService usageStatQueryService) : IVoteSessionQueryService
{
    public async Task<IReadOnlyList<VoteSessionSummaryDto>> ListSessionsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();

        return await db.VoteSessions
            .Where(s => s.Channel.ChannelName == normalized)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new VoteSessionSummaryDto(s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<VoteSessionSummaryDto>> ListSessionsPagedAsync(string channelName, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();
        var query = db.VoteSessions.Where(s => s.Channel.ChannelName == normalized);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new VoteSessionSummaryDto(s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<VoteSessionSummaryDto>(items, page, pageSize, totalCount);
    }

    public async Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, string? viewerTwitchUserId = null, bool includeRawUsage = false, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();

        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var activeEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
            .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl })
            .ToListAsync(cancellationToken);

        var myVotesByEmoteId = viewerTwitchUserId is null
            ? new Dictionary<string, VoteType>()
            : await db.Votes
                .Where(v => v.VoteSessionId == sessionId && v.UserId == viewerTwitchUserId)
                .ToDictionaryAsync(v => v.EmoteId, v => v.Type, cancellationToken);

        var from = DateOnly.FromDateTime(session.StartedAt);
        var to = DateOnly.FromDateTime(session.EndedAt ?? DateTime.UtcNow);

        var usageTotals = activeEmotes.Count == 0
            ? []
            : await usageStatQueryService.GetUsageTotalsAsync(normalized, from, to, cancellationToken);
        var usageByEmoteId = usageTotals.ToDictionary(u => u.EmoteId, u => u.TotalUseCount);

        var voteTallies = await db.Votes
            .Where(v => v.VoteSessionId == sessionId)
            .GroupBy(v => v.EmoteId)
            .Select(g => new
            {
                EmoteId = g.Key,
                Keep = g.Count(v => v.Type == VoteType.Keep),
                Delete = g.Count(v => v.Type == VoteType.Delete)
            })
            .ToDictionaryAsync(g => g.EmoteId, cancellationToken);

        var useCounts = activeEmotes.Select(e => usageByEmoteId.GetValueOrDefault(e.Id, 0)).ToList();
        var min = useCounts.Count == 0 ? 0 : useCounts.Min();
        var max = useCounts.Count == 0 ? 0 : useCounts.Max();

        var results = activeEmotes.Select(e =>
        {
            var useCount = usageByEmoteId.GetValueOrDefault(e.Id, 0);
            var normalizedUsage = max == min ? 0d : (useCount - min) / (double)(max - min) * 100d;
            var tally = voteTallies.GetValueOrDefault(e.Id);
            var keep = tally?.Keep ?? 0;
            var delete = tally?.Delete ?? 0;
            var score = normalizedUsage + (keep - delete);
            var myVote = myVotesByEmoteId.TryGetValue(e.Id, out var voteType) ? voteType : (VoteType?)null;

            // Normalisation and score still use the real useCount — only the reported figure is
            // withheld, so a non-manager sees identical ranking without the absolute numbers.
            return new VoteSessionResultDto(
                e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, includeRawUsage ? useCount : 0, normalizedUsage, keep, delete, score, myVote);
        })
        .OrderByDescending(r => r.Score)
        .ToList();

        return new VoteSessionResultsDto(session.Id, session.Title, session.IsActive, session.StartedAt, session.EndedAt, results);
    }

    public async Task<PagedResult<MyVoteSessionDto>> ListMyVoteSessionsAsync(string voterTwitchUserId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // Group over Votes first, then join back to VoteSessions/Channel — grouping directly on a
        // navigation-joined query has previously failed to translate in this codebase (see
        // UsageStatQueryService.GetUsageTotalsAsync's decision-log entry), so the reduction to a
        // scalar (VoteSessionId, LastVotedAt) pair happens before any join.
        var votedSessionIds = db.Votes
            .Where(v => v.UserId == voterTwitchUserId)
            .GroupBy(v => v.VoteSessionId)
            .Select(g => new { SessionId = g.Key, LastVotedAt = g.Max(v => v.UpdatedAt) });

        var joined = votedSessionIds.Join(
            db.VoteSessions,
            x => x.SessionId,
            s => s.Id,
            (x, s) => new { x.LastVotedAt, s.Id, s.Title, ChannelName = s.Channel.ChannelName, s.IsActive, s.StartedAt, s.EndedAt });

        var totalCount = await joined.CountAsync(cancellationToken);
        var items = await joined
            .OrderByDescending(x => x.LastVotedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new MyVoteSessionDto(x.Id, x.Title, x.ChannelName, x.IsActive, x.StartedAt, x.EndedAt, x.LastVotedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<MyVoteSessionDto>(items, page, pageSize, totalCount);
    }
}
