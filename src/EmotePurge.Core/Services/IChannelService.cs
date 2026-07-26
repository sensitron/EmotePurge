using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public interface IChannelService
{
    Task<Channel> JoinAsync(string channelName, CancellationToken cancellationToken = default);

    Task<bool> LeaveAsync(string channelName, CancellationToken cancellationToken = default);

    Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default);

    // Admin-only overview — no filtering/paging (channel count is expected to stay small).
    Task<IReadOnlyList<Channel>> ListAllAsync(CancellationToken cancellationToken = default);
}
