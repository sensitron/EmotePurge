using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class ChannelAccessService(
    IModeratorCheckService moderatorCheckService,
    ISevenTvApiClient sevenTvApiClient,
    IChannelService channelService,
    IModRoleCache modRoleCache,
    IConfiguration configuration,
    ILogger<ChannelAccessService> logger) : IChannelAccessService
{
    public async Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = channelName.Trim().ToLowerInvariant();

        if (IsGlobalAdmin(principal))
        {
            return true;
        }

        var channel = await channelService.GetByNameAsync(normalizedChannel, cancellationToken);
        if (IsBroadcaster(principal, normalizedChannel, channel?.TwitchChannelId))
        {
            return true;
        }

        return await moderatorCheckService.IsModeratorAsync(principal, normalizedChannel, cancellationToken);
    }

    public async Task<bool> CanViewUsageStatsAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        if (await CanManageChannelAsync(principal, channelName, cancellationToken))
        {
            return true;
        }

        var normalizedChannel = channelName.Trim().ToLowerInvariant();

        // This was the most expensive authorization path in the app — two sequential, uncached 7TV
        // calls per request, on endpoints a viewer can poll freely. Cached like the moderator check,
        // same TTL and same staleness trade-off.
        var cached = await modRoleCache.TryGetIsSevenTvEditorAsync(principal.TwitchUserId, normalizedChannel, cancellationToken);
        if (cached is { } isEditorCached)
        {
            return isEditorCached;
        }

        var identity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(principal.TwitchUserId, cancellationToken);
        if (identity is null)
        {
            // Not cached: a 7TV outage means "unknown", and storing it as "no" would lock genuine
            // editors out for the whole TTL.
            return false;
        }

        var editorOf = await sevenTvApiClient.GetEditorOfChannelsAsync(identity.SevenTvUserId, cancellationToken);
        if (editorOf is null)
        {
            return false;
        }

        // Matched on the immutable Twitch id where we have one, for the same reason as IsBroadcaster.
        var channel = await channelService.GetByNameAsync(normalizedChannel, cancellationToken);
        var isEditor = channel?.TwitchChannelId is { } channelTwitchId
            ? editorOf.Any(grant => string.Equals(grant.TwitchChannelId, channelTwitchId, StringComparison.Ordinal))
            : editorOf.Any(grant => string.Equals(grant.TwitchChannelLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase));

        await modRoleCache.SetIsSevenTvEditorAsync(principal.TwitchUserId, normalizedChannel, isEditor, cancellationToken);
        return isEditor;
    }

    public bool IsGlobalAdmin(TwitchPrincipalInfo principal)
    {
        var adminLogins = configuration.GetSection("Auth:AdminTwitchLogins").Get<string[]>() ?? [];
        return adminLogins.Any(login => string.Equals(login, principal.TwitchLogin, StringComparison.OrdinalIgnoreCase));
    }

    // Twitch permits renames and releases the old name again after a grace period. Deciding "is
    // broadcaster" on the login alone therefore handed the channel to whoever registered the freed-up
    // name next: a pure string comparison, without consulting Twitch, 7TV or the database, granting
    // full usage statistics, vote-session control and the channel's entire history. The immutable
    // numeric id is authoritative wherever we have one; the login comparison survives only as a
    // fallback for rows whose TwitchChannelId has never been resolved (nullable until the first sync).
    private bool IsBroadcaster(TwitchPrincipalInfo principal, string normalizedChannel, string? channelTwitchId)
    {
        if (channelTwitchId is null)
        {
            return string.Equals(principal.TwitchLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(channelTwitchId, principal.TwitchUserId, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(principal.TwitchLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase))
        {
            // Either the channel was renamed and this is its new owner, or the stored id is wrong.
            // Denied either way, but logged: otherwise this is a rejection nobody could explain.
            logger.LogWarning(
                "Broadcaster-Zugriff auf {Channel} abgelehnt: Login stimmt überein, aber die hinterlegte Twitch-ID {ChannelTwitchId} weicht von der des Nutzers ({UserTwitchId}) ab.",
                normalizedChannel, channelTwitchId, principal.TwitchUserId);
        }

        return false;
    }
}
