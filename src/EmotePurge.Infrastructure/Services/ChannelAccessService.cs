using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;

namespace EmotePurge.Infrastructure.Services;

public class ChannelAccessService(
    IModeratorCheckService moderatorCheckService,
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

    public bool IsGlobalAdmin(TwitchPrincipalInfo principal)
    {
        var adminLogins = configuration.GetSection("Auth:AdminTwitchLogins").Get<string[]>() ?? [];
        return adminLogins.Any(login => string.Equals(login, principal.TwitchLogin, StringComparison.OrdinalIgnoreCase));
    }
}
