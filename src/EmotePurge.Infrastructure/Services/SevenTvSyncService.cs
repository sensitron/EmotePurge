using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class SevenTvSyncService(
    AppDbContext db,
    ISevenTvApiClient sevenTvApiClient,
    IEmoteMatchCache emoteMatchCache,
    ILogger<SevenTvSyncService> logger)
    : ISevenTvSyncService
{
    public async Task<string?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = channelName.Trim().ToLowerInvariant();

        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            logger.LogWarning("SyncChannelAsync: {Channel} nicht in Postgres gefunden.", normalized);
            return null;
        }

        var twitchUserId = channel.TwitchChannelId
            ?? await sevenTvApiClient.ResolveTwitchUserIdAsync(normalized, cancellationToken);
        if (twitchUserId is null)
        {
            return null;
        }

        var emoteSet = await sevenTvApiClient.GetEmoteSetForTwitchUserAsync(twitchUserId, cancellationToken);
        if (emoteSet is null)
        {
            return null;
        }

        channel.TwitchChannelId ??= twitchUserId;
        channel.ActiveEmoteSetId = emoteSet.Id;

        await ReconcileAsync(channel.Id, emoteSet.Emotes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        return emoteSet.Id;
    }

    public async Task ApplyEmoteSetUpdateAsync(string emoteSetId, SevenTvEmoteSetDelta delta, CancellationToken cancellationToken = default)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.ActiveEmoteSetId == emoteSetId, cancellationToken);
        if (channel is null)
        {
            logger.LogWarning("7TV-Dispatch für unbekanntes Set {SetId} ignoriert.", emoteSetId);
            return;
        }

        var existing = await db.Emotes
            .Where(e => e.ChannelId == channel.Id)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        foreach (var emote in delta.Added)
        {
            UpsertEmote(channel.Id, existing, emote);
        }

        foreach (var emote in delta.Updated)
        {
            UpsertEmote(channel.Id, existing, emote);
        }

        foreach (var removedId in delta.RemovedIds)
        {
            if (existing.TryGetValue(removedId, out var emote))
            {
                emote.IsArchived = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);
    }

    private async Task RefreshMatchCacheAsync(Channel channel, CancellationToken cancellationToken)
    {
        var activeEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
            .Select(e => new { e.Name, e.Id })
            .ToListAsync(cancellationToken);

        // 7TV active sets can legitimately contain two emotes sharing the same chat alias
        // (observed live) — ToDictionary would throw, so duplicates are coalesced instead,
        // keeping whichever was loaded first and logging the collision.
        var emoteNameToId = new Dictionary<string, string>();
        foreach (var emote in activeEmotes)
        {
            if (!emoteNameToId.TryAdd(emote.Name, emote.Id))
            {
                logger.LogWarning(
                    "Doppelter aktiver Emote-Name {Name} in Channel {Channel} — Chat-Matching zählt nur auf die zuerst geladene Emote-Id.",
                    emote.Name, channel.ChannelName);
            }
        }

        emoteMatchCache.ReplaceChannel(channel.ChannelName, emoteNameToId);
    }

    private async Task ReconcileAsync(string channelId, IReadOnlyList<SevenTvEmote> liveEmotes, CancellationToken cancellationToken)
    {
        var existing = await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        var liveIds = liveEmotes.Select(e => e.Id).ToHashSet();

        foreach (var emote in liveEmotes)
        {
            UpsertEmote(channelId, existing, emote);
        }

        foreach (var (sevenTvEmoteId, emote) in existing)
        {
            if (!liveIds.Contains(sevenTvEmoteId) && !emote.IsArchived)
            {
                emote.IsArchived = true;
            }
        }
    }

    private void UpsertEmote(string channelId, Dictionary<string, Emote> existing, SevenTvEmote live)
    {
        if (existing.TryGetValue(live.Id, out var emote))
        {
            if (emote.Name != live.Name || emote.ImageUrl != live.ImageUrl || emote.IsArchived)
            {
                emote.Name = live.Name;
                emote.ImageUrl = live.ImageUrl;
                emote.IsArchived = false;
                emote.LastSyncedAt = DateTime.UtcNow;
            }
        }
        else
        {
            emote = new Emote
            {
                ChannelId = channelId,
                SevenTvEmoteId = live.Id,
                Name = live.Name,
                ImageUrl = live.ImageUrl
            };
            db.Emotes.Add(emote);
            existing[live.Id] = emote;
        }
    }
}
