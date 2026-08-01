using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class ChannelAccessService(
    IModeratorCheckService moderatorCheckService,
    ISevenTvEditorService sevenTvEditorService,
    IChannelService channelService,
    IConfiguration configuration,
    ILogger<ChannelAccessService> logger) : IChannelAccessService
{
    public async Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);

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

        var normalizedChannel = ChannelName.Normalize(channelName);

        // Resolution and caching of the grants live in ISevenTvEditorService — this used to be the
        // most expensive authorization path in the app (two sequential 7TV calls per request, on
        // endpoints a viewer can poll freely) and one of two independent copies of the same chain.
        var grants = await sevenTvEditorService.GetEditorGrantsAsync(principal.TwitchUserId, cancellationToken);
        if (grants is null)
        {
            return false;
        }

        // Matched on the immutable Twitch id where we have one, for the same reason as IsBroadcaster.
        var channel = await channelService.GetByNameAsync(normalizedChannel, cancellationToken);
        return channel?.TwitchChannelId is { } channelTwitchId
            ? grants.TwitchChannelIds.Contains(channelTwitchId)
            : grants.ChannelLogins.Contains(normalizedChannel);
    }

    public bool IsGlobalAdmin(TwitchPrincipalInfo principal)
    {
        return GetAdminLogins(configuration)
            .Any(login => string.Equals(login, principal.TwitchLogin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Accepts both shapes on purpose, and the scalar one wins. A JSON array in appsettings.json
    /// lands on the indexed keys <c>Auth:AdminTwitchLogins:0..</c>, while an environment variable
    /// or user-secret can only ever set the plain key <c>Auth:AdminTwitchLogins</c>. Those are
    /// different keys, so a naive Get&lt;string[]&gt;() would keep returning the appsettings array
    /// and silently ignore the override — which is exactly how the allow-list ended up being
    /// editable only by changing a file in the repo.
    /// </summary>
    private static string[] GetAdminLogins(IConfiguration configuration)
    {
        var scalar = configuration["Auth:AdminTwitchLogins"];
        return string.IsNullOrWhiteSpace(scalar)
            ? configuration.GetSection("Auth:AdminTwitchLogins").Get<string[]>() ?? []
            : scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
