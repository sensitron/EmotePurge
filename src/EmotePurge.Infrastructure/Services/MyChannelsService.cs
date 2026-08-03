using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class MyChannelsService(
    AppDbContext db,
    ITwitchHelixClient helixClient,
    ISevenTvEditorService sevenTvEditorService,
    ITwitchUserTokenService userTokenService,
    ITwitchLiveStatusReader liveStatusReader) : IMyChannelsService
{
    private sealed class ChannelFlags
    {
        public bool IsBroadcaster;
        public bool IsModerator;
        public bool IsSevenTvEditor;
    }

    public async Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        var selfLogin = ChannelName.Normalize(principal.TwitchLogin);

        // Helix's moderated-channels list only ever contains channels the user moderates for
        // someone else — it never includes the channel the user broadcasts themselves.
        var flagsByChannel = new Dictionary<string, ChannelFlags> { [selfLogin] = new() { IsBroadcaster = true } };
        var helixUnavailable = false;

        var token = await userTokenService.GetValidAccessTokenAsync(principal, cancellationToken);
        var reauthRequired = token.ReauthRequired;
        if (token.AccessToken is null)
        {
            helixUnavailable = true;
        }
        else
        {
            var moderatedChannels = await helixClient.GetModeratedChannelLoginsAsync(token.AccessToken, principal.TwitchUserId, cancellationToken);
            if (moderatedChannels is null)
            {
                helixUnavailable = true;
            }
            else
            {
                foreach (var login in moderatedChannels)
                {
                    var normalized = ChannelName.Normalize(login);
                    GetOrAdd(flagsByChannel, normalized).IsModerator = true;
                }
            }
        }

        // Independent of the Twitch-role axis above — a 7TV editor grant doesn't require any Twitch
        // relationship at all, so this can add brand-new channel keys, not just annotate existing ones.
        // Shares ISevenTvEditorService (and therefore its cache) with the authorization path; this used
        // to run the same two-call chain here, uncached, on every single overview load.
        var grants = await sevenTvEditorService.GetEditorGrantsAsync(principal.TwitchUserId, cancellationToken);
        var sevenTvUnavailable = grants is null;
        foreach (var login in grants?.ChannelLogins ?? Enumerable.Empty<string>())
        {
            GetOrAdd(flagsByChannel, login).IsSevenTvEditor = true;
        }

        var trackedChannels = await db.Channels
            .AsNoTracking()
            .Where(c => flagsByChannel.Keys.Contains(c.ChannelName))
            .Select(c => new { c.ChannelName, c.IsBotActive })
            .ToDictionaryAsync(c => c.ChannelName, c => c.IsBotActive, cancellationToken);

        // The worker's last live-poll result; only bot-active channels were polled, so for every
        // other row absence from the live set must read as "unknown", not "offline".
        var liveStatus = await liveStatusReader.ReadAsync(cancellationToken);
        var liveLogins = liveStatus is null ? null : liveStatus.LiveChannelLogins.ToHashSet();

        var channels = flagsByChannel
            .Select(kv => new MyChannelDto(
                kv.Key,
                kv.Value.IsBroadcaster,
                kv.Value.IsModerator,
                kv.Value.IsSevenTvEditor,
                IsTracked: trackedChannels.ContainsKey(kv.Key),
                IsBotActive: trackedChannels.GetValueOrDefault(kv.Key, false),
                LiveState: ChannelLiveStates.Derive(liveLogins, kv.Key, trackedChannels.GetValueOrDefault(kv.Key, false))))
            .OrderByDescending(c => c.IsBroadcaster)
            .ThenBy(c => c.ChannelName)
            .ToList();

        return new MyChannelsResultDto(helixUnavailable, reauthRequired, sevenTvUnavailable, channels, liveStatus?.GeneratedAtUtc);
    }

    private static ChannelFlags GetOrAdd(Dictionary<string, ChannelFlags> flagsByChannel, string channelName)
    {
        if (!flagsByChannel.TryGetValue(channelName, out var flags))
        {
            flags = new ChannelFlags();
            flagsByChannel[channelName] = flags;
        }

        return flags;
    }
}
