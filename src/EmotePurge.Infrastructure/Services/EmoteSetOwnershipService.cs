using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetOwnershipService(
    AppDbContext db,
    ISevenTvApiClient sevenTvApiClient,
    ITwitchHelixClient twitchHelixClient,
    ITwitchUserTokenService userTokenService,
    ILogger<EmoteSetOwnershipService> logger) : IEmoteSetOwnershipService
{
    // Bounds how many parallel 7TV lookups Tier 3 fires off for a single check — a prolific mod's
    // moderated-channel list could otherwise flood 7TV with dozens of simultaneous requests.
    private const int MaxConcurrentTier3Lookups = 5;

    public async Task<EmoteSetWarningDto> CheckAsync(
        string channelName,
        TwitchPrincipalInfo? caller,
        CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);

        if (channel is null || channel.TwitchChannelId is null || string.IsNullOrEmpty(channel.ActiveEmoteSetId))
        {
            return new EmoteSetWarningDto(Available: false, IsOwnSet: false, [], []);
        }

        // Tier 1: does this channel's own 7TV account actually own its currently active set?
        var ownIdentity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(channel.TwitchChannelId, cancellationToken);
        var setOwnerId = await sevenTvApiClient.GetEmoteSetOwnerIdAsync(channel.ActiveEmoteSetId, cancellationToken);

        var available = ownIdentity is not null && setOwnerId is not null;
        var isOwnSet = available && string.Equals(ownIdentity!.SevenTvUserId, setOwnerId, StringComparison.Ordinal);

        if (!available)
        {
            logger.LogWarning("Owner-Check für Channel {Channel} / Set {SetId} nicht möglich (7TV nicht erreichbar oder unbekannt).",
                normalized, channel.ActiveEmoteSetId);
        }

        // Tier 2: other channels WE already track that happen to share the same active set.
        var otherTracked = await db.Channels.AsNoTracking()
            .Where(c => c.ActiveEmoteSetId == channel.ActiveEmoteSetId && c.Id != channel.Id)
            .Select(c => c.ChannelName)
            .ToListAsync(cancellationToken);

        // Tier 3: channels the acting user moderates on Twitch but that we've never tracked/synced —
        // Tier 2 can't see these at all, since they have no row (or a stale one) in our own DB.
        var otherModerated = await CheckModeratedChannelsAsync(
            channel.ActiveEmoteSetId, normalized, otherTracked, caller, cancellationToken);

        return new EmoteSetWarningDto(available, isOwnSet, otherTracked, otherModerated);
    }

    private async Task<IReadOnlyList<string>> CheckModeratedChannelsAsync(
        string activeEmoteSetId,
        string currentChannelName,
        IReadOnlyList<string> alreadyCoveredByTier2,
        TwitchPrincipalInfo? caller,
        CancellationToken cancellationToken)
    {
        if (caller is null)
        {
            return [];
        }

        var token = await userTokenService.GetValidAccessTokenAsync(caller, cancellationToken);
        if (token.AccessToken is null)
        {
            return [];
        }

        var moderated = await twitchHelixClient.GetModeratedChannelsAsync(token.AccessToken, caller.TwitchUserId, cancellationToken);
        if (moderated is null || moderated.Count == 0)
        {
            return [];
        }

        var alreadyCovered = new HashSet<string>(alreadyCoveredByTier2, StringComparer.OrdinalIgnoreCase) { currentChannelName };
        var candidates = moderated.Where(c => !alreadyCovered.Contains(c.Login)).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        using var semaphore = new SemaphoreSlim(MaxConcurrentTier3Lookups);
        var matches = new List<string>();
        var matchesLock = new object();

        var lookups = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var identity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(candidate.BroadcasterId, cancellationToken);
                if (identity?.ActiveEmoteSetId == activeEmoteSetId)
                {
                    lock (matchesLock)
                    {
                        matches.Add(candidate.Login);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(lookups);
        return matches;
    }
}
