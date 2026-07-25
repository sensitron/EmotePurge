using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public interface IUserService
{
    Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default);
}
