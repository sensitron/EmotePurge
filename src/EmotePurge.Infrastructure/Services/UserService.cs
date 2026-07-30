using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UserService(AppDbContext db, ITokenCipher tokenCipher) : IUserService
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

    public async Task StoreTwitchTokensAsync(string twitchUserId, string accessToken, DateTime accessTokenExpiresAtUtc, string refreshToken, string? scopes, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            // The login callback upserts the user before storing tokens, so this only happens if a
            // refresh races a user deletion — nothing sensible to attach the tokens to then.
            return;
        }

        user.TwitchRefreshToken = tokenCipher.Protect(refreshToken);
        user.TwitchAccessToken = tokenCipher.Protect(accessToken);
        user.TwitchAccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
        user.TwitchTokenScopes = scopes;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TwitchStoredTokens?> GetTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == twitchUserId)
            .Select(u => new { u.TwitchRefreshToken, u.TwitchAccessToken, u.TwitchAccessTokenExpiresAtUtc, u.TwitchTokenScopes })
            .SingleOrDefaultAsync(cancellationToken);

        if (row?.TwitchRefreshToken is null)
        {
            return null;
        }

        var refreshToken = tokenCipher.Unprotect(row.TwitchRefreshToken);
        if (refreshToken is null)
        {
            // Undecryptable (rotated key, tampered row) — same consequence as no stored token.
            return null;
        }

        var accessToken = row.TwitchAccessToken is null ? null : tokenCipher.Unprotect(row.TwitchAccessToken);
        return new TwitchStoredTokens(
            refreshToken,
            accessToken,
            accessToken is null ? null : row.TwitchAccessTokenExpiresAtUtc,
            row.TwitchTokenScopes);
    }

    public async Task ClearTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            return;
        }

        user.TwitchRefreshToken = null;
        user.TwitchAccessToken = null;
        user.TwitchAccessTokenExpiresAtUtc = null;
        user.TwitchTokenScopes = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
