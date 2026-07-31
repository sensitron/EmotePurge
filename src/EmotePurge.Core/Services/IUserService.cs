using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

// Decrypted view of the per-user Twitch token columns. AccessToken/ExpiresAtUtc may be null
// (never refreshed yet, or the stored ciphertext failed to decrypt) while RefreshToken is not —
// a row without a decryptable refresh token is reported as null instead.
public record TwitchStoredTokens(string RefreshToken, string? AccessToken, DateTime? AccessTokenExpiresAtUtc, string? Scopes);

public interface IUserService
{
    Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default);

    // Cutoff for server-side session revocation; null means nothing was ever revoked.
    Task<DateTime?> GetSessionsValidFromUtcAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Invalidates every session issued before now for this user; false when the user is unknown.
    // `actor` decides whether the revocation is audited: an admin forcing another user out passes
    // themselves and a `user.revokeSessions` entry is written in the same transaction; self-logout
    // passes null and stays unaudited (user decision: no login/logout events in the audit log).
    // Deliberately not defaulted — every caller must make that choice visibly.
    Task<bool> RevokeSessionsAsync(string twitchUserId, AuditActor? actor, CancellationToken cancellationToken = default);

    // Persists a fresh token pair (login callback or successful refresh). Tokens are encrypted
    // via ITokenCipher before they touch the database; callers always pass plaintext.
    Task StoreTwitchTokensAsync(string twitchUserId, string accessToken, DateTime accessTokenExpiresAtUtc, string refreshToken, string? scopes, CancellationToken cancellationToken = default);

    // Null when the user is unknown, has no stored refresh token, or the stored value cannot be
    // decrypted (key change/tamper) — all three mean "refreshing is impossible, re-login required".
    Task<TwitchStoredTokens?> GetTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Drops all stored Twitch tokens (logout, or Twitch reported the refresh token as invalid).
    Task ClearTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Admin action: drops every cached role answer (mod/sub/7TV editor) for this user via
    // IModRoleCache, so the next authorization check resolves live. Returns the number of removed
    // cache entries, or null when the user is unknown. Audited as user.invalidateRoleCache; the
    // actor is required — unlike session revocation there is no self-service variant of this.
    Task<int?> InvalidateRoleCacheAsync(string twitchUserId, AuditActor actor, CancellationToken cancellationToken = default);
}
