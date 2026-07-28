using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Configuration;

namespace EmotePurge.Infrastructure.Services;

public class ChannelAccessService(
    IModeratorCheckService moderatorCheckService,
    ISevenTvApiClient sevenTvApiClient,
    IConfiguration configuration) : IChannelAccessService
{
    public async Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = channelName.Trim().ToLowerInvariant();

        if (IsGlobalAdmin(principal))
        {
            return true;
        }

        if (string.Equals(principal.TwitchLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase))
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

        var identity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(principal.TwitchUserId, cancellationToken);
        if (identity is null)
        {
            return false;
        }

        var editorOf = await sevenTvApiClient.GetEditorOfChannelsAsync(identity.SevenTvUserId, cancellationToken);
        return editorOf?.Any(grant => string.Equals(grant.TwitchChannelLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    public bool IsGlobalAdmin(TwitchPrincipalInfo principal)
    {
        var adminLogins = configuration.GetSection("Auth:AdminTwitchLogins").Get<string[]>() ?? [];
        return adminLogins.Any(login => string.Equals(login, principal.TwitchLogin, StringComparison.OrdinalIgnoreCase));
    }
}
