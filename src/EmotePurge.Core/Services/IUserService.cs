using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public interface IUserService
{
    Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default);

    // Cutoff for server-side session revocation; null means nothing was ever revoked.
    Task<DateTime?> GetSessionsValidFromUtcAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Invalidates every session issued before now for this user.
    Task RevokeSessionsAsync(string twitchUserId, CancellationToken cancellationToken = default);
}
