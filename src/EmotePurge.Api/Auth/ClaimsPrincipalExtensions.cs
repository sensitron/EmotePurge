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

        // Access token expires ~4h after login (no refresh flow in this pass — see plan/decision
        // log); once it's expired we simply stop attempting live Helix role checks.
        var accessToken = user.FindFirstValue(TwitchClaimTypes.AccessToken);
        var expiresAtRaw = user.FindFirstValue(TwitchClaimTypes.TokenExpiresAtUtc);
        var tokenStillValid = expiresAtRaw is not null
            && DateTime.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAt)
            && expiresAt > DateTime.UtcNow;

        return new TwitchPrincipalInfo(twitchUserId, twitchLogin, tokenStillValid ? accessToken : null);
    }
}
