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
        var normalized = channelName.Trim().ToLowerInvariant();

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
        var normalized = channelName.Trim().ToLowerInvariant();

        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(cancellationToken);
        await redisPublisher.PublishAsync(CommandsChannel, $"LEAVE:{normalized}", cancellationToken);

        return true;
    }

    public async Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();
        return await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
    }
}
