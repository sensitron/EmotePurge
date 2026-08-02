using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public enum ChannelResyncResult
{
    Triggered,
    NotFound,
    NotActive,
}

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

    // The normalized names of every channel the bot is currently meant to be in — the worker's
    // boot recovery and its periodic 7TV resync both start from this list. Exists as a service
    // method rather than as the identical inline query both hosted services used to carry, because
    // "which channels are active?" is a domain question and because the direct AppDbContext access
    // it replaced was the one place in the repo that stepped around the layering rule.
    Task<IReadOnlyList<string>> ListActiveChannelNamesAsync(CancellationToken cancellationToken = default);

    // Publishes a RESYNC command for an active channel, making the worker re-resolve the full 7TV
    // truth immediately instead of waiting for the next periodic tick. Fire-and-forget by design:
    // the command protocol is one-way, so "triggered" means "published", not "completed" — the
    // admin channel list's LastSyncedAtUtc is where completion becomes visible. Restricted to
    // active channels: the worker's sync path would otherwise create an EventAPI subscription for
    // a channel the bot is not even in.
    Task<ChannelResyncResult> TriggerResyncAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);
}
