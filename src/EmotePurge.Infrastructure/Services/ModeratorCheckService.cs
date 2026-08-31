using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// Answers one authorization question from the shared moderated-channel list. Neither the Helix
// pagination nor the caching lives here any more: both are IModeratedChannelsProvider's job, which
// is also what removed the old per-user-and-channel bool cache — that one paid a full pagination
// for every channel the same user asked about.
public class ModeratorCheckService(
    IModeratedChannelsProvider moderatedChannelsProvider,
    ILogger<ModeratorCheckService> logger) : IModeratorCheckService
{
    public async Task<bool> IsModeratorAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);

        var lookup = await moderatedChannelsProvider.GetModeratedChannelsAsync(principal, cancellationToken);
        if (lookup.Channels is null)
        {
            // Not the same as "moderates nothing": no usable token, a transient Helix failure or an
            // unfinished pagination. Denied for this request and logged, because an outage must not
            // be indistinguishable from a confirmed no. Nothing is cached in this case, so the next
            // request resolves live again.
            logger.LogInformation(
                "Mod-Check für {User}/{Channel} verweigert: moderierte Channels nicht ermittelbar (Reauth nötig: {ReauthRequired}).",
                principal.TwitchUserId, normalizedChannel, lookup.ReauthRequired);
            return false;
        }

        // An empty list is a real answer and simply means no.
        return lookup.Channels.Any(channel => string.Equals(channel.Login, normalizedChannel, StringComparison.OrdinalIgnoreCase));
    }
}
