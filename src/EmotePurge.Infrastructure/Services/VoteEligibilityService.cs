using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class VoteEligibilityService(
    AppDbContext db,
    IModeratorCheckService moderatorCheckService,
    ITwitchHelixClient helixClient,
    ILogger<VoteEligibilityService> logger) : IVoteEligibilityService
{
    public async Task<VoteEligibilityResult> EvaluateAsync(
        TwitchPrincipalInfo principal, string channelName, long sessionId, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = channelName.Trim().ToLowerInvariant();
        var (channel, session) = await LoadSessionAsync(normalizedChannel, sessionId, cancellationToken);
        if (channel is null || session is null)
        {
            return VoteEligibilityResult.SessionNotFound;
        }

        if (!session.IsActive)
        {
            return VoteEligibilityResult.SessionEnded;
        }

        return await EvaluateRoleAsync(principal, normalizedChannel, channel, session, cancellationToken);
    }

    public async Task<VoteEligibilityResult> EvaluateAudienceAsync(
        TwitchPrincipalInfo principal, string channelName, long sessionId, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = channelName.Trim().ToLowerInvariant();
        var (channel, session) = await LoadSessionAsync(normalizedChannel, sessionId, cancellationToken);
        if (channel is null || session is null)
        {
            return VoteEligibilityResult.SessionNotFound;
        }

        return await EvaluateRoleAsync(principal, normalizedChannel, channel, session, cancellationToken);
    }

    private async Task<(Channel? Channel, VoteSession? Session)> LoadSessionAsync(
        string normalizedChannel, long sessionId, CancellationToken cancellationToken)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalizedChannel, cancellationToken);
        if (channel is null)
        {
            return (null, null);
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        return (channel, session);
    }

    private async Task<VoteEligibilityResult> EvaluateRoleAsync(
        TwitchPrincipalInfo principal, string normalizedChannel, Channel channel, VoteSession session, CancellationToken cancellationToken)
    {
        var roles = session.AllowedVoterRoles;

        if (roles.HasFlag(AllowedRoles.Everyone))
        {
            return VoteEligibilityResult.Allowed;
        }

        // Broadcaster/Mods can always vote in their own channel's sessions, regardless of which
        // roles a session otherwise targets — same "channel management" precedence as join/leave
        // (ChannelAccessService), not gated behind the AllowedVoterRoles flags like Subs/Everyone.
        if (string.Equals(principal.TwitchLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase))
        {
            return VoteEligibilityResult.Allowed;
        }

        if (await moderatorCheckService.IsModeratorAsync(principal, normalizedChannel, cancellationToken))
        {
            return VoteEligibilityResult.Allowed;
        }

        if (roles.HasFlag(AllowedRoles.Subs))
        {
            if (channel.TwitchChannelId is null || principal.AccessToken is null)
            {
                logger.LogInformation(
                    "Sub-Check für {User}/{Channel} übersprungen: TwitchChannelId oder Access Token fehlt.",
                    principal.TwitchUserId, normalizedChannel);
            }
            else
            {
                var isSubscribed = await helixClient.GetUserSubscriptionStatusAsync(
                    principal.AccessToken, channel.TwitchChannelId, principal.TwitchUserId, cancellationToken);
                if (isSubscribed == true)
                {
                    return VoteEligibilityResult.Allowed;
                }
            }
        }

        return VoteEligibilityResult.RoleNotEligible;
    }
}
