using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public interface IChannelService
{
    Task<Channel> JoinAsync(string channelName, CancellationToken cancellationToken = default);

    // Deactivates the bot for this channel and keeps the row and all its history. Reversible via
    // JoinAsync. See PurgeAsync for the irreversible variant.
    Task<bool> LeaveAsync(string channelName, CancellationToken cancellationToken = default);

    // Irreversibly deletes the channel row and, by cascade, its emotes, usage statistics, vote
    // sessions and votes. Admin-only by design — see the endpoint.
    Task<bool> PurgeAsync(string channelName, CancellationToken cancellationToken = default);

    Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default);

    // Admin-only overview — no filtering/paging (channel count is expected to stay small).
    Task<IReadOnlyList<Channel>> ListAllAsync(CancellationToken cancellationToken = default);
}
