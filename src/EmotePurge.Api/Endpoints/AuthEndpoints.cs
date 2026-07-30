using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace EmotePurge.Api.Endpoints;

public static class AuthEndpoints
{
    private const string OAuthStateCookieName = "ep_oauth_state";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/twitch/login", (HttpContext httpContext, IConfiguration configuration) =>
        {
            var clientId = configuration["Auth:Twitch:ClientId"]
                ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:ClientId' fehlt.");
            var redirectUri = configuration["Auth:Twitch:RedirectUri"]
                ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:RedirectUri' fehlt.");

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            httpContext.Response.Cookies.Append(OAuthStateCookieName, state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = httpContext.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddMinutes(5)
            });

            var authorizeUrl = "https://id.twitch.tv/oauth2/authorize" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(TwitchOAuthDefaults.RequestedScopes)}" +
                $"&state={state}";

            return Results.Redirect(authorizeUrl);
        });

        group.MapGet("/twitch/callback", async (
            HttpContext httpContext,
            string? code,
            string? state,
            IConfiguration configuration,
            ITwitchAuthClient authClient,
            ITwitchHelixClient helixClient,
            IUserService userService,
            CancellationToken ct) =>
        {
            var expectedState = httpContext.Request.Cookies[OAuthStateCookieName];
            httpContext.Response.Cookies.Delete(OAuthStateCookieName);

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || expectedState is null || state != expectedState)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidOAuthState });
            }

            var redirectUri = configuration["Auth:Twitch:RedirectUri"]
                ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:RedirectUri' fehlt.");

            var token = await authClient.ExchangeAuthorizationCodeAsync(code, redirectUri, ct);
            if (token is null)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.TwitchTokenExchangeFailed });
            }

            var userInfo = await helixClient.GetUserInfoAsync(token.AccessToken, ct);
            if (userInfo is null)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.TwitchUserInfoUnavailable });
            }

            await userService.UpsertLoginAsync(userInfo.Id, userInfo.Login, userInfo.DisplayName, ct);

            // Stored server-side (encrypted) so the on-demand refresh flow can outlive the ~4h
            // access-token claim below. If Twitch ever omits the refresh token, any previously
            // stored one is kept — it may still be valid.
            if (token.RefreshToken is not null)
            {
                await userService.StoreTwitchTokensAsync(
                    userInfo.Id, token.AccessToken, token.ExpiresAtUtc, token.RefreshToken, token.Scopes, ct);
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userInfo.Id),
                new(TwitchClaimTypes.Login, userInfo.Login),
                new(TwitchClaimTypes.DisplayName, userInfo.DisplayName),
                new(TwitchClaimTypes.AccessToken, token.AccessToken),
                new(TwitchClaimTypes.TokenExpiresAtUtc, token.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                // Compared against User.SessionsValidFromUtc on every request (OnValidatePrincipal).
                // One second of slack: the claim is written before SignInAsync, and a logout landing
                // in the same second must not invalidate the session being created right now.
                new(TwitchClaimTypes.SessionIssuedAtUtc,
                    DateTime.UtcNow.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture))
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            var postLoginRedirectUrl = configuration["Auth:Twitch:PostLoginRedirectUrl"] ?? "/";
            return Results.Redirect(postLoginRedirectUrl);
        });

        group.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            twitchUserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
            login = user.FindFirstValue(TwitchClaimTypes.Login),
            displayName = user.FindFirstValue(TwitchClaimTypes.DisplayName),
            tokenExpiresAtUtc = user.FindFirstValue(TwitchClaimTypes.TokenExpiresAtUtc)
        })).RequireAuthorization();

        group.MapPost("/logout", async (
            HttpContext httpContext,
            IUserService userService,
            CancellationToken ct) =>
        {
            // Revoke server-side as well, not just delete the browser's copy: the cookie stays
            // cryptographically valid otherwise, and with persisted Data Protection keys nothing
            // else would ever invalidate it. Deliberately not behind RequireAuthorization — an
            // already-expired session must still be able to clear its cookie.
            var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (twitchUserId is not null)
            {
                await userService.RevokeSessionsAsync(twitchUserId, ct);
                // Logout is already global per user (the revocation above kills every session), so
                // the server also gives up its ability to refresh Twitch tokens on this user's behalf.
                await userService.ClearTwitchTokensAsync(twitchUserId, ct);
            }

            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
    }
}
