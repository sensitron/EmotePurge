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
}
