using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// See ITwitchUserTokenService for the contract. Order of preference per call:
//   1. the cookie-claim token while its claimed expiry holds (no DB touch — first ~4h of a login),
//   2. the stored server-side access token (60s safety margin),
//   3. a single-flight refresh against Twitch, persisting whatever refresh token comes back.
// Every handed-out token older than an hour since its last check is run through /oauth2/validate
// first (Twitch's hard "validate hourly" rule) — which also detects password-change/disconnect
// revocation within the hour instead of only at expiry.
public class TwitchUserTokenService(
    IUserService userService,
    ITwitchAuthClient authClient,
    TwitchTokenRefreshGate gate,
    ILogger<TwitchUserTokenService> logger) : ITwitchUserTokenService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ValidateInterval = TimeSpan.FromHours(1);

    public async Task<TwitchUserTokenResult> GetValidAccessTokenAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        // Fast path: TryBuildTwitchPrincipal already nulls the claim token once its expiry passed.
        if (principal.AccessToken is not null
            && await StillValidPerTwitchAsync(principal.TwitchUserId, principal.AccessToken, cancellationToken))
        {
            return new TwitchUserTokenResult(principal.AccessToken, ReauthRequired: false);
        }

        var tokens = await userService.GetTwitchTokensAsync(principal.TwitchUserId, cancellationToken);
        if (tokens is null)
        {
            // Logged in before the refresh flow existed (or tokens were cleared) — nothing to refresh.
            return new TwitchUserTokenResult(null, ReauthRequired: true);
        }

        if (ScopesDrifted(tokens.Scopes))
        {
            return new TwitchUserTokenResult(null, ReauthRequired: true);
        }

        if (IsStillFresh(tokens)
            && await StillValidPerTwitchAsync(principal.TwitchUserId, tokens.AccessToken!, cancellationToken))
        {
            return new TwitchUserTokenResult(tokens.AccessToken, ReauthRequired: false);
        }

        return await RefreshSingleFlightAsync(principal.TwitchUserId, cancellationToken);
    }

    private async Task<TwitchUserTokenResult> RefreshSingleFlightAsync(string twitchUserId, CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(twitchUserId, cancellationToken);

        // Double-check under the lock: a parallel request may have refreshed while we waited.
        var tokens = await userService.GetTwitchTokensAsync(twitchUserId, cancellationToken);
        if (tokens is null)
        {
            return new TwitchUserTokenResult(null, ReauthRequired: true);
        }

        if (IsStillFresh(tokens))
        {
            // Freshly refreshed moments ago — definitionally validated, no extra /validate call.
            return new TwitchUserTokenResult(tokens.AccessToken, ReauthRequired: false);
        }

        var refresh = await authClient.RefreshUserTokenAsync(tokens.RefreshToken, cancellationToken);
        switch (refresh.Status)
        {
            case TwitchRefreshStatus.Success:
                var token = refresh.Token!;
                // Twitch may rotate the refresh token ("refresh tokens may change") — always keep
                // whatever came back, falling back to the one that just worked.
                var newScopes = token.Scopes ?? tokens.Scopes;
                await userService.StoreTwitchTokensAsync(
                    twitchUserId, token.AccessToken, token.ExpiresAtUtc,
                    token.RefreshToken ?? tokens.RefreshToken, newScopes, cancellationToken);
                gate.MarkValidated(twitchUserId);
                logger.LogInformation("Twitch-Access-Token für {User} per Refresh erneuert (gültig bis {ExpiresAt:O}).",
                    twitchUserId, token.ExpiresAtUtc);

                if (ScopesDrifted(newScopes))
                {
                    return new TwitchUserTokenResult(null, ReauthRequired: true);
                }

                return new TwitchUserTokenResult(token.AccessToken, ReauthRequired: false);

            case TwitchRefreshStatus.InvalidGrant:
                // Password change, app disconnect, or Twitch-side revoke — refreshing can never
                // succeed again. Drop the stored pair so the state is visible as "re-login needed".
                logger.LogWarning("Twitch-Refresh-Token für {User} ist ungültig — gespeicherte Tokens werden verworfen, Re-Login nötig.", twitchUserId);
                await userService.ClearTwitchTokensAsync(twitchUserId, cancellationToken);
                return new TwitchUserTokenResult(null, ReauthRequired: true);

            default:
                // Transient — keep the refresh token, degrade this one call like a Helix outage.
                logger.LogInformation("Twitch-Refresh für {User} transient fehlgeschlagen — nächster Bedarf versucht es erneut.", twitchUserId);
                return new TwitchUserTokenResult(null, ReauthRequired: false);
        }
    }

    // Hourly validate-on-use. A transient validate failure counts as "still valid" — availability
    // must not degrade because id.twitch.tv had a hiccup; the timestamp then stays unset, so the
    // next call simply validates again.
    private async Task<bool> StillValidPerTwitchAsync(string twitchUserId, string accessToken, CancellationToken cancellationToken)
    {
        if (!gate.NeedsValidation(twitchUserId, ValidateInterval))
        {
            return true;
        }

        var valid = await authClient.ValidateTokenAsync(accessToken, cancellationToken);
        if (valid is false)
        {
            logger.LogInformation("Twitch-Access-Token für {User} laut /validate nicht mehr gültig — Refresh wird versucht.", twitchUserId);
            return false;
        }

        if (valid is true)
        {
            gate.MarkValidated(twitchUserId);
        }

        return true;
    }

    private static bool IsStillFresh(TwitchStoredTokens tokens)
    {
        return tokens.AccessToken is not null
            && tokens.AccessTokenExpiresAtUtc is { } expiresAt
            && expiresAt - ExpirySkew > DateTime.UtcNow;
    }

    // Lenient by design: only scopes we request but the stored grant lacks force a re-login.
    // Null stored scopes (rows from before scope tracking) are trusted until the next refresh.
    private static bool ScopesDrifted(string? storedScopes)
    {
        if (storedScopes is null)
        {
            return false;
        }

        var stored = storedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return TwitchOAuthDefaults.RequestedScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Except(stored, StringComparer.Ordinal)
            .Any();
    }
}
