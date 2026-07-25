namespace EmotePurge.Core.Services;

public interface IModRoleCache
{
    // null = cache miss, caller must resolve live and call Set.
    Task<bool?> TryGetIsModeratorAsync(string twitchUserId, string channelName, CancellationToken cancellationToken = default);

    Task SetIsModeratorAsync(string twitchUserId, string channelName, bool isModerator, CancellationToken cancellationToken = default);
}
