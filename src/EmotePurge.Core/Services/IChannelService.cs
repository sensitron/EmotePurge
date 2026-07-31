using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public interface IChannelService
{
    // All three write methods take the acting user: each writes its own AuditLogEntry into the same
    // transaction as the change itself (see the implementations). The actor is a required parameter
    // rather than an optional one so a new call site cannot silently produce unattributed history.
    Task<Channel> JoinAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);

    // Deactivates the bot for this channel and keeps the row and all its history. Reversible via
    // JoinAsync. See PurgeAsync for the irreversible variant.
    Task<bool> LeaveAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);

    // Irreversibly deletes the channel row and, by cascade, its emotes, usage statistics, vote
    // sessions and votes. Admin-only by design — see the endpoint. The audit entry deliberately
    // outlives the channel (AuditLogEntry.ChannelName is a snapshot, not an FK).
    Task<bool> PurgeAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);

    Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default);
}
