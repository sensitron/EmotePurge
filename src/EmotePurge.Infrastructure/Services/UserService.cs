using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UserService(AppDbContext db) : IUserService
{
    public async Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            user = new User { Id = twitchUserId, TwitchUsername = twitchUsername, DisplayName = displayName };
            db.Users.Add(user);
        }
        else
        {
            user.TwitchUsername = twitchUsername;
            user.DisplayName = displayName;
            user.LastLogin = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<DateTime?> GetSessionsValidFromUtcAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        // Runs on every authenticated request (see OnValidatePrincipal): a single primary-key lookup,
        // projected to the one column so it stays an index-only read and nothing gets tracked.
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == twitchUserId)
            .Select(u => u.SessionsValidFromUtc)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task RevokeSessionsAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            return;
        }

        user.SessionsValidFromUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
