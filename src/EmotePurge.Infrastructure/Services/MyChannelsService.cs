using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class MyChannelsService(
    AppDbContext db,
    ITwitchHelixClient helixClient,
    ISevenTvApiClient sevenTvApiClient) : IMyChannelsService
{
    private sealed class ChannelFlags
    {
        public bool IsBroadcaster;
        public bool IsModerator;
        public bool IsSevenTvEditor;
    }

    public async Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        var selfLogin = principal.TwitchLogin.Trim().ToLowerInvariant();

        // Helix's moderated-channels list only ever contains channels the user moderates for
        // someone else — it never includes the channel the user broadcasts themselves.
        var flagsByChannel = new Dictionary<string, ChannelFlags> { [selfLogin] = new() { IsBroadcaster = true } };
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
                    GetOrAdd(flagsByChannel, normalized).IsModerator = true;
                }
            }
        }

        // Independent of the Twitch-role axis above — a 7TV editor grant doesn't require any Twitch
        // relationship at all, so this can add brand-new channel keys, not just annotate existing ones.
        var sevenTvUnavailable = false;
        var identity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(principal.TwitchUserId, cancellationToken);
        if (identity is null)
        {
            sevenTvUnavailable = true;
        }
        else
        {
            var editorOf = await sevenTvApiClient.GetEditorOfChannelsAsync(identity.SevenTvUserId, cancellationToken);
            if (editorOf is null)
            {
                sevenTvUnavailable = true;
            }
            else
            {
                foreach (var grant in editorOf)
                {
                    var normalized = grant.TwitchChannelLogin.Trim().ToLowerInvariant();
                    GetOrAdd(flagsByChannel, normalized).IsSevenTvEditor = true;
                }
            }
        }

        var trackedChannels = await db.Channels
            .AsNoTracking()
            .Where(c => flagsByChannel.Keys.Contains(c.ChannelName))
            .Select(c => new { c.ChannelName, c.IsBotActive })
            .ToDictionaryAsync(c => c.ChannelName, c => c.IsBotActive, cancellationToken);

        var channels = flagsByChannel
            .Select(kv => new MyChannelDto(
                kv.Key,
                kv.Value.IsBroadcaster,
                kv.Value.IsModerator,
                kv.Value.IsSevenTvEditor,
                IsTracked: trackedChannels.ContainsKey(kv.Key),
                IsBotActive: trackedChannels.GetValueOrDefault(kv.Key, false)))
            .OrderByDescending(c => c.IsBroadcaster)
            .ThenBy(c => c.ChannelName)
            .ToList();

        return new MyChannelsResultDto(helixUnavailable, sevenTvUnavailable, channels);
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
