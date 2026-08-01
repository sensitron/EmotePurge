using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionQueryService(AppDbContext db, IUsageStatQueryService usageStatQueryService) : IVoteSessionQueryService
{
    public async Task<IReadOnlyList<VoteSessionSummaryDto>> ListSessionsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var sessions = await db.VoteSessions
            .Where(s => s.Channel.ChannelName == normalized)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new { s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, EmoteCount = s.SessionEmotes.Count })
            .ToListAsync(cancellationToken);

        // 0 membership rows = dynamic "all emotes" session; the DTO reports that as null, not 0.
        return sessions
            .Select(s => new VoteSessionSummaryDto(
                s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, s.EmoteCount == 0 ? null : s.EmoteCount))
            .ToList();
    }

    public async Task<PagedResult<VoteSessionSummaryDto>> ListSessionsPagedAsync(string channelName, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);
        var query = db.VoteSessions.Where(s => s.Channel.ChannelName == normalized);

        var totalCount = await query.CountAsync(cancellationToken);
        var pageRows = await query
            .OrderByDescending(s => s.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new { s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, EmoteCount = s.SessionEmotes.Count })
            .ToListAsync(cancellationToken);

        // 0 membership rows = dynamic "all emotes" session; the DTO reports that as null, not 0.
        var items = pageRows
            .Select(s => new VoteSessionSummaryDto(
                s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, s.EmoteCount == 0 ? null : s.EmoteCount))
            .ToList();

        return new PagedResult<VoteSessionSummaryDto>(items, page, pageSize, totalCount);
    }

    public async Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, string? viewerTwitchUserId = null, bool includeRawUsage = false, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var (channel, session) = await db.LoadChannelSessionAsync(normalized, sessionId, cancellationToken);
        if (channel is null || session is null)
        {
            return null;
        }

        var subsetEmoteIds = await db.VoteSessionEmotes
            .Where(se => se.VoteSessionId == sessionId)
            .Select(se => se.EmoteId)
            .ToListAsync(cancellationToken);

        // No membership rows = dynamic "all emotes" session: archived emotes vanish from the results,
        // exactly as before the subset feature. An explicit ballot keeps its archived members visible
        // (badged in the UI, voting on them closed) so a curated list never loses entries silently.
        var candidateEmotes = subsetEmoteIds.Count == 0
            ? await db.Emotes
                .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
                .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.IsArchived })
                .ToListAsync(cancellationToken)
            : await db.Emotes
                .Where(e => e.ChannelId == channel.Id && subsetEmoteIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.IsArchived })
                .ToListAsync(cancellationToken);

        var myVotesByEmoteId = viewerTwitchUserId is null
            ? new Dictionary<string, VoteType>()
            : await db.Votes
                .Where(v => v.VoteSessionId == sessionId && v.UserId == viewerTwitchUserId)
                .ToDictionaryAsync(v => v.EmoteId, v => v.Type, cancellationToken);

        var from = DateOnly.FromDateTime(session.StartedAt);
        var to = DateOnly.FromDateTime(session.EndedAt ?? DateTime.UtcNow);

        // Usage is manager-only context now and no part of the score, so the totals query can be
        // skipped entirely for everyone else.
        var usageTotals = !includeRawUsage || candidateEmotes.Count == 0
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

        var voterCount = await db.Votes
            .Where(v => v.VoteSessionId == sessionId)
            .Select(v => v.UserId)
            .Distinct()
            .CountAsync(cancellationToken);

        var results = candidateEmotes.Select(e =>
        {
            var tally = voteTallies.GetValueOrDefault(e.Id);
            var keep = tally?.Keep ?? 0;
            var delete = tally?.Delete ?? 0;
            var myVote = myVotesByEmoteId.TryGetValue(e.Id, out var voteType) ? voteType : (VoteType?)null;

            // null = withheld (non-manager) or not computed: GetUsageTotalsAsync excludes archived
            // emotes, and reporting a fabricated 0 for an archived ballot member would just be wrong.
            int? useCount = includeRawUsage && !e.IsArchived ? usageByEmoteId.GetValueOrDefault(e.Id, 0) : null;

            return new VoteSessionResultDto(
                e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, useCount, keep, delete, keep - delete, e.IsArchived, myVote);
        })
        // Delete candidates first: ascending net score, contested emotes before quiet ties, name as
        // the stable fallback so equal rows don't reshuffle between loads.
        .OrderBy(r => r.Score)
        .ThenByDescending(r => r.KeepVotes + r.DeleteVotes)
        .ThenBy(r => r.EmoteName, StringComparer.OrdinalIgnoreCase)
        .ToList();

        return new VoteSessionResultsDto(session.Id, session.Title, session.IsActive, session.StartedAt, session.EndedAt, voterCount, results);
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
