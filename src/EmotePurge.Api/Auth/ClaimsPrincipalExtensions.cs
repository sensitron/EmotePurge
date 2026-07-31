using System.Globalization;
using System.Security.Claims;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

internal static class ClaimsPrincipalExtensions
{
    public static TwitchPrincipalInfo? TryBuildTwitchPrincipal(this ClaimsPrincipal user)
    {
        var twitchUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var twitchLogin = user.FindFirstValue(TwitchClaimTypes.Login);
        if (twitchUserId is null || twitchLogin is null)
        {
            return null;
        }

        // The claim token expires ~4h after login. Past that, AccessToken is null and
        // ITwitchUserTokenService transparently takes over via the stored refresh token.
        var accessToken = user.FindFirstValue(TwitchClaimTypes.AccessToken);
        var expiresAtRaw = user.FindFirstValue(TwitchClaimTypes.TokenExpiresAtUtc);
        var tokenStillValid = expiresAtRaw is not null
            && DateTime.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAt)
            && expiresAt > DateTime.UtcNow;

        return new TwitchPrincipalInfo(twitchUserId, twitchLogin, tokenStillValid ? accessToken : null);
    }

    /// <summary>
    /// The caller as the audit log records them: id plus login, both snapshotted at action time.
    /// Null exactly when <see cref="TryBuildTwitchPrincipal"/> is — an unauthenticated request, which
    /// none of the audited endpoints can be reached by (they all sit behind
    /// <c>RequireAuthorization()</c>), so the null branch is a guard, not a case.
    /// </summary>
    public static AuditActor? TryBuildAuditActor(this ClaimsPrincipal user)
    {
        var twitchUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var twitchLogin = user.FindFirstValue(TwitchClaimTypes.Login);
        return twitchUserId is null || twitchLogin is null
            ? null
            : new AuditActor(twitchUserId, twitchLogin);
    }
}
