using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The channel-scoped lookups the services need, in one place. Deliberately not a generic
/// repository — just the exact shapes that were being copied by hand.
/// <para>
/// The reason this matters beyond duplication: <c>s.ChannelId == channel.Id</c> is the *only* thing
/// stopping channel A from ending, deleting or voting on a session belonging to channel B. It was
/// written out six times, and only one of those six was covered by a test.
/// </para>
/// <para>
/// The second reason is Regel 9: every lookup has to filter on the *normalized* name. Nine call
/// sites outside the vote-session services were still normalizing by hand — which worked, but meant
/// nine chances for the next one to forget.
/// </para>
/// </summary>
internal static class ChannelQueries
{
    /// <summary>
    /// Loads a channel by its (un-normalized) name. Tracked — callers that mutate what they load
    /// depend on it. Use <see cref="LoadChannelReadOnlyAsync"/> for pure reads.
    /// </summary>
    public static Task<Channel?> LoadChannelAsync(this AppDbContext db, string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
    }

    /// <summary>
    /// Untracked counterpart for read-only paths. Separate method rather than a bool parameter, so
    /// the call site says which one it is without the reader checking an argument.
    /// </summary>
    public static Task<Channel?> LoadChannelReadOnlyAsync(this AppDbContext db, string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
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

    /// <summary>
    /// Loads a channel by its Twitch id: once a caller has a Twitch id in hand, it is looking for
    /// the *channel*, not for whatever name it currently answers to, and a rename can leave a
    /// second row under a new name sharing that same id. Tracked, since the callers that need this
    /// (rename reconciliation) mutate what they find. Twitch ids are opaque digit strings, never
    /// <see cref="ChannelName.Normalize"/>d.
    /// </summary>
    public static Task<Channel?> LoadChannelByTwitchIdAsync(
        this AppDbContext db, string twitchChannelId, CancellationToken cancellationToken) =>
        db.Channels.SingleOrDefaultAsync(c => c.TwitchChannelId == twitchChannelId, cancellationToken);
}
