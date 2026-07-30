using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The two channel-scoped lookups that every vote-session service needs, in one place. Deliberately
/// not a generic repository — just the exact two shapes that were copied by hand six times across
/// three services.
/// <para>
/// The reason this matters beyond duplication: <c>s.ChannelId == channel.Id</c> is the *only* thing
/// stopping channel A from ending, deleting or voting on a session belonging to channel B. It was
/// written out six times, and only one of those six was covered by a test.
/// </para>
/// </summary>
internal static class ChannelQueries
{
    /// <summary>
    /// Loads a channel by its (un-normalized) name. Tracked, like every call site was before —
    /// callers that mutate what they load depend on it.
    /// </summary>
    public static Task<Channel?> LoadChannelAsync(this AppDbContext db, string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
    }

    /// <summary>
    /// Loads a channel and one of *its* vote sessions. Returns <c>(null, null)</c> for an unknown
    /// channel and <c>(channel, null)</c> when the session exists but belongs to a different channel —
    /// callers must treat the latter as "not found" and never as "found but foreign".
    /// </summary>
    public static async Task<(Channel? Channel, VoteSession? Session)> LoadChannelSessionAsync(
        this AppDbContext db, string channelName, long sessionId, CancellationToken cancellationToken)
    {
        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return (null, null);
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        return (channel, session);
    }
}
