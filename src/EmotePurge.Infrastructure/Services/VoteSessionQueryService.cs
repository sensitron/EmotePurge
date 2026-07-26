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

    public async Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, string? viewerTwitchUserId = null, CancellationToken cancellationToken = default)
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

            return new VoteSessionResultDto(e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, useCount, normalizedUsage, keep, delete, score, myVote);
        })
        .OrderByDescending(r => r.Score)
        .ToList();

        return new VoteSessionResultsDto(session.Id, session.Title, session.IsActive, session.StartedAt, session.EndedAt, results);
    }
}
