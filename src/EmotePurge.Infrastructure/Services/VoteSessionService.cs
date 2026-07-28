using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionService(AppDbContext db) : IVoteSessionService
{
    public async Task<VoteSession?> CreateAsync(string channelName, string title, AllowedRoles allowedVoterRoles, DateTime? startedAt = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title must not be empty.", nameof(title));
        }

        if (allowedVoterRoles == 0)
        {
            throw new ArgumentException("AllowedVoterRoles must not be empty.", nameof(allowedVoterRoles));
        }

        if (allowedVoterRoles.HasFlag(AllowedRoles.VIPs))
        {
            throw new ArgumentException("AllowedRoles.VIPs is not supported (no Twitch self-check API available).", nameof(allowedVoterRoles));
        }

        if (startedAt is { } requestedStartedAt && requestedStartedAt > DateTime.UtcNow)
        {
            throw new ArgumentException("StartedAt must not be in the future.", nameof(startedAt));
        }

        var normalized = channelName.Trim().ToLowerInvariant();
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        var session = new VoteSession
        {
            ChannelId = channel.Id,
            Title = title.Trim(),
            AllowedVoterRoles = allowedVoterRoles,
            StartedAt = startedAt ?? DateTime.UtcNow
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        return session;
    }

    public async Task<VoteSession?> EndAsync(string channelName, long sessionId, CancellationToken cancellationToken = default)
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

        if (session.IsActive)
        {
            session.IsActive = false;
            session.EndedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return session;
    }

    public async Task<(VoteCastResult Result, Vote? Vote)> CastVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, VoteType type, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return (VoteCastResult.ChannelNotFound, null);
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        if (session is null)
        {
            return (VoteCastResult.SessionNotFound, null);
        }

        if (!session.IsActive)
        {
            return (VoteCastResult.SessionEnded, null);
        }

        var emoteExists = await db.Emotes.AnyAsync(
            e => e.Id == emoteId && e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);
        if (!emoteExists)
        {
            return (VoteCastResult.EmoteNotEligible, null);
        }

        var vote = await db.Votes.SingleOrDefaultAsync(
            v => v.VoteSessionId == sessionId && v.EmoteId == emoteId && v.UserId == voterTwitchUserId, cancellationToken);
        if (vote is null)
        {
            vote = new Vote
            {
                VoteSessionId = sessionId,
                EmoteId = emoteId,
                UserId = voterTwitchUserId,
                Type = type
            };
            db.Votes.Add(vote);
        }
        else
        {
            vote.Type = type;
            vote.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (VoteCastResult.Success, vote);
    }

    public async Task<VoteCastResult> RetractVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return VoteCastResult.ChannelNotFound;
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        if (session is null)
        {
            return VoteCastResult.SessionNotFound;
        }

        if (!session.IsActive)
        {
            return VoteCastResult.SessionEnded;
        }

        var vote = await db.Votes.SingleOrDefaultAsync(
            v => v.VoteSessionId == sessionId && v.EmoteId == emoteId && v.UserId == voterTwitchUserId, cancellationToken);
        if (vote is not null)
        {
            db.Votes.Remove(vote);
            await db.SaveChangesAsync(cancellationToken);
        }

        return VoteCastResult.Success;
    }

    public async Task<bool> DeleteAsync(string channelName, long sessionId, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        if (session is null)
        {
            return false;
        }

        db.VoteSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
