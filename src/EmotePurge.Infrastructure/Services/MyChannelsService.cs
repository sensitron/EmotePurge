using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class MyChannelsService(AppDbContext db, ITwitchHelixClient helixClient) : IMyChannelsService
{
    public async Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        var selfLogin = principal.TwitchLogin.Trim().ToLowerInvariant();

        // Helix's moderated-channels list only ever contains channels the user moderates for
        // someone else — it never includes the channel the user broadcasts themselves.
        var roleByChannel = new Dictionary<string, ChannelRole> { [selfLogin] = ChannelRole.Broadcaster };
        var helixUnavailable = false;

        if (principal.AccessToken is null)
        {
            helixUnavailable = true;
        }
        else
        {
            var moderatedChannels = await helixClient.GetModeratedChannelLoginsAsync(principal.AccessToken, principal.TwitchUserId, cancellationToken);
            if (moderatedChannels is null)
            {
                helixUnavailable = true;
            }
            else
            {
                foreach (var login in moderatedChannels)
                {
                    var normalized = login.Trim().ToLowerInvariant();
                    if (!roleByChannel.ContainsKey(normalized))
                    {
                        roleByChannel[normalized] = ChannelRole.Moderator;
                    }
                }
            }
        }

        var trackedChannels = await db.Channels
            .AsNoTracking()
            .Where(c => roleByChannel.Keys.Contains(c.ChannelName))
            .Select(c => new { c.ChannelName, c.IsBotActive })
            .ToDictionaryAsync(c => c.ChannelName, c => c.IsBotActive, cancellationToken);

        var channels = roleByChannel
            .Select(kv => new MyChannelDto(
                kv.Key,
                kv.Value,
                IsTracked: trackedChannels.ContainsKey(kv.Key),
                IsBotActive: trackedChannels.GetValueOrDefault(kv.Key, false)))
            .OrderByDescending(c => c.Role == ChannelRole.Broadcaster)
            .ThenBy(c => c.ChannelName)
            .ToList();

        return new MyChannelsResultDto(helixUnavailable, channels);
    }
}
