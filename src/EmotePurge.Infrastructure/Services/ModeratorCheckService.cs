using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class ModeratorCheckService(
    ITwitchHelixClient helixClient,
    IModRoleCache modRoleCache,
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

        if (principal.AccessToken is null)
        {
            // Access token expired and no refresh flow in this pass — the live Helix check
            // simply cannot run; the caller must re-login to regain moderator-level access.
            logger.LogInformation("Mod-Check für {User}/{Channel} übersprungen: kein gültiger Access Token.", principal.TwitchUserId, normalizedChannel);
            return false;
        }

        var moderatedChannels = await helixClient.GetModeratedChannelLoginsAsync(principal.AccessToken, principal.TwitchUserId, cancellationToken);
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
