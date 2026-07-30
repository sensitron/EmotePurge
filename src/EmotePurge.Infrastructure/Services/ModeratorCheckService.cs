using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class ModeratorCheckService(
    ITwitchHelixClient helixClient,
    IModRoleCache modRoleCache,
    ITwitchUserTokenService userTokenService,
    ILogger<ModeratorCheckService> logger) : IModeratorCheckService
{
    public async Task<bool> IsModeratorAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);

        var cached = await modRoleCache.TryGetIsModeratorAsync(principal.TwitchUserId, normalizedChannel, cancellationToken);
        if (cached is not null)
        {
            return cached.Value;
        }

        // After the cache check on purpose — a cache hit must never cost a token refresh.
        var token = await userTokenService.GetValidAccessTokenAsync(principal, cancellationToken);
        if (token.AccessToken is null)
        {
            // No usable token even after the refresh attempt — the live Helix check simply
            // cannot run; deny without caching so a later refresh/re-login retries live.
            logger.LogInformation("Mod-Check für {User}/{Channel} übersprungen: kein gültiger Access Token.", principal.TwitchUserId, normalizedChannel);
            return false;
        }

        var moderatedChannels = await helixClient.GetModeratedChannelLoginsAsync(token.AccessToken, principal.TwitchUserId, cancellationToken);
        if (moderatedChannels is null)
        {
            // Transient Helix failure — deny, but don't cache it, so the next request retries live.
            return false;
        }

        var isModerator = moderatedChannels.Contains(normalizedChannel);
        await modRoleCache.SetIsModeratorAsync(principal.TwitchUserId, normalizedChannel, isModerator, cancellationToken);
        return isModerator;
    }
}
