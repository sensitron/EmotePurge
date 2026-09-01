using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class ChannelService(AppDbContext db, IRedisPublisher redisPublisher) : IChannelService
{
    public async Task<Channel> JoinAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            channel = new Channel { ChannelName = normalized, IsBotActive = true };
            db.Channels.Add(channel);
        }
        else
        {
            // Only a join that actually reactivates the channel restarts the tracking clock. A join
            // on an already-active channel is a no-op for coverage — it publishes a JOIN command,
            // but nothing was ever missed, so moving the marker would falsely shorten the history
            // we claim to have.
            if (!channel.IsBotActive)
            {
                channel.TrackingResumedAt = DateTime.UtcNow;
            }

            channel.IsBotActive = true;
        }

        // Audited unconditionally, including the "already active" case: every join publishes a JOIN
        // command and makes the worker (re)enter the channel, so something did happen even when the
        // row itself is unchanged. This is the one place the no-op rule does not apply.
        db.AddAuditEntry(actor, AuditActions.ChannelJoin, channelName: normalized);

        await db.SaveChangesAsync(cancellationToken);
        await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.JoinPrefix}{normalized}", cancellationToken);

        return channel;
    }

    public async Task<bool> LeaveAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
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
        // Only reached for a channel that exists — the unknown-channel branch above returns without
        // touching anything and therefore without an entry.
        db.AddAuditEntry(actor, AuditActions.ChannelLeave, channelName: normalized);
        await db.SaveChangesAsync(cancellationToken);
        // Committed before published: if this throws (Redis outage), the row is already the source
        // of truth and SevenTvPeriodicResyncWorker's prune step (RosterPrunePolicy, issue #41) picks
        // the channel up within one resync interval regardless — this publish is an acceleration, not
        // a prerequisite. Same is true for JoinAsync below and TriggerResyncAsync via the periodic
        // sync loop itself; only this method needed a new convergence net, since JOIN/RESYNC already
        // had one.
        await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{normalized}", cancellationToken);

        return true;
    }

    public async Task<bool> PurgeAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        // The deliberate hard delete, cascading through emotes, usage stats, vote sessions and
        // votes. Publishes LEAVE first so the worker stops matching chat for a channel whose
        // emotes are about to disappear.
        //
        // Issue #41 checked this ordering rather than assuming it: unlike JoinAsync/LeaveAsync/
        // TriggerResyncAsync, the publish here already precedes SaveChangesAsync, so a Redis outage
        // aborts the method (500) before anything is written — consistent, if unavailable, and left
        // unchanged. A dedicated 503 for that case was considered and deferred (docs/DECISIONS.md,
        // 2026-09-01): lowest-priority of the three points in #41, and today's UnexpectedError/500
        // is at least honest about "nothing happened".
        await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{normalized}", cancellationToken);
        // Staged before the Remove and committed with it: the entry is the only trace this channel
        // ever existed once the cascade has run, which is exactly why AuditLogEntry.ChannelName is a
        // snapshot string and not a foreign key — an FK would have cascaded this row away too.
        db.AddAuditEntry(actor, AuditActions.ChannelPurge, channelName: normalized);
        db.Channels.Remove(channel);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default)
    {
        return await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListActiveChannelNamesAsync(CancellationToken cancellationToken = default)
    {
        // AsNoTracking because both callers only ever read the names: this runs once per minute
        // forever in SevenTvPeriodicResyncWorker, and tracking entities nobody mutates is pure cost.
        return await db.Channels
            .AsNoTracking()
            .Where(c => c.IsBotActive)
            .Select(c => c.ChannelName)
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChannelResyncResult> TriggerResyncAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return ChannelResyncResult.NotFound;
        }

        if (!channel.IsBotActive)
        {
            // See the interface remarks: syncing an inactive channel would subscribe its emote set
            // on the EventAPI while nothing consumes the events. No audit entry — nothing happened.
            return ChannelResyncResult.NotActive;
        }

        // Audited like JoinAsync's always-audit case: the row is untouched, but a command is
        // published and the worker will do real work because of it.
        db.AddAuditEntry(actor, AuditActions.ChannelResync, channelName: normalized);
        await db.SaveChangesAsync(cancellationToken);
        await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.ResyncPrefix}{normalized}", cancellationToken);

        return ChannelResyncResult.Triggered;
    }
}
