using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class ChannelService(AppDbContext db, IRedisPublisher redisPublisher) : IChannelService
{
    private const string CommandsChannel = "channel:bot:commands";

    public async Task<Channel> JoinAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            channel = new Channel { ChannelName = normalized, IsBotActive = true };
            db.Channels.Add(channel);
        }
        else
        {
            channel.IsBotActive = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        await redisPublisher.PublishAsync(CommandsChannel, $"JOIN:{normalized}", cancellationToken);

        return channel;
    }

    public async Task<bool> LeaveAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        // Soft deactivate, not Remove(): the row hangs on four cascade edges (Channel -> Emote,
        // Emote -> UsageStat, Channel -> VoteSession, VoteSession -> Vote, Emote -> Vote), so a
        // hard delete threw away every emote, the entire daily usage history since the bot joined,
        // all vote sessions and all cast votes — none of it reconstructible, since 7TV only ever
        // returns the *current* set and past Twitch chat cannot be queried after the fact. A leave
        // is an operational action a moderator may perform; destroying history is not.
        // SevenTvPeriodicResyncWorker and Worker's boot recovery both filter on IsBotActive, and
        // JoinAsync reactivates the row, so nothing else needs to change.
        channel.IsBotActive = false;
        await db.SaveChangesAsync(cancellationToken);
        await redisPublisher.PublishAsync(CommandsChannel, $"LEAVE:{normalized}", cancellationToken);

        return true;
    }

    public async Task<bool> PurgeAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        // The deliberate hard delete, cascading through emotes, usage stats, vote sessions and
        // votes. Publishes LEAVE first so the worker stops matching chat for a channel whose
        // emotes are about to disappear.
        await redisPublisher.PublishAsync(CommandsChannel, $"LEAVE:{normalized}", cancellationToken);
        db.Channels.Remove(channel);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);
        return await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<Channel>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        return await db.Channels.AsNoTracking().OrderBy(c => c.ChannelName).ToListAsync(cancellationToken);
    }
}
