using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionService(AppDbContext db) : IVoteSessionService
{
    public async Task<(CreateVoteSessionResult Result, VoteSession? Session)> CreateAsync(
        string channelName, string title, AllowedRoles allowedVoterRoles, DateTime? startedAt = null, CancellationToken cancellationToken = default)
    {
        // Validated here rather than in the endpoint: this is the layer that has tests, and it is the
        // one every future caller goes through. The endpoint only maps these results to error codes.
        if (string.IsNullOrWhiteSpace(title))
        {
            return (CreateVoteSessionResult.TitleEmpty, null);
        }

        if (allowedVoterRoles == 0)
        {
            return (CreateVoteSessionResult.RolesEmpty, null);
        }

        // Not a design choice: Twitch has no self-report endpoint for VIP status, so a voter cannot
        // prove their own (see the decision log).
        if (allowedVoterRoles.HasFlag(AllowedRoles.VIPs))
        {
            return (CreateVoteSessionResult.VipsNotSupported, null);
        }

        if (startedAt is { } requestedStartedAt)
        {
            if (requestedStartedAt > DateTime.UtcNow)
            {
                return (CreateVoteSessionResult.StartedAtInFuture, null);
            }

            if (requestedStartedAt < DateTime.UtcNow.AddDays(-VoteSessionLimits.MaxBackdateDays))
            {
                return (CreateVoteSessionResult.StartedAtTooFarBack, null);
            }
        }

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return (CreateVoteSessionResult.ChannelNotFound, null);
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

        return (CreateVoteSessionResult.Success, session);
    }

    public async Task<VoteSession?> EndAsync(string channelName, long sessionId, CancellationToken cancellationToken = default)
    {
        var (_, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
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
        var (channel, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (channel is null)
        {
            return (VoteCastResult.ChannelNotFound, null);
        }

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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Read-then-insert loses a race against a second request from the same user: a double click
            // sends both clicks down the insert path (the first has not committed when the second reads),
            // and the (VoteSessionId, EmoteId, UserId) unique index rejects the loser — a 500 for what is
            // in truth an idempotent action. Reload and apply it as the update it always was.
            db.Entry(vote).State = EntityState.Detached;

            var existing = await db.Votes.SingleOrDefaultAsync(
                v => v.VoteSessionId == sessionId && v.EmoteId == emoteId && v.UserId == voterTwitchUserId, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            existing.Type = type;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return (VoteCastResult.Success, existing);
        }

        return (VoteCastResult.Success, vote);
    }

    public async Task<VoteCastResult> RetractVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, CancellationToken cancellationToken = default)
    {
        var (channel, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (channel is null)
        {
            return VoteCastResult.ChannelNotFound;
        }

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
        var (_, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        db.VoteSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
