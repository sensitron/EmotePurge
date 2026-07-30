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
    ChannelSyncGate channelSyncGate,
    ILogger<SevenTvSyncService> logger)
    : ISevenTvSyncService
{
    public async Task<string?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        // Two concurrent syncs of the same channel collide on the (ChannelId, SevenTvEmoteId)
        // unique index — see ChannelSyncGate.
        using var gate = await channelSyncGate.AcquireAsync(normalized, cancellationToken);

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

        // A successful response with an empty emote list is indistinguishable from a real set wipe,
        // but the consequences are wildly asymmetric: ReconcileAsync would archive every emote of
        // the channel and RefreshMatchCacheAsync would install an empty dictionary, so chat
        // matching stops entirely until the next successful sync (up to 60s — thousands of lost
        // matches at HandOfBlood's message rate). Known triggers: a set change in progress, a
        // partial 7TV outage, an owner briefly emptying the set. Treated as implausible and
        // skipped; the next tick recovers on its own.
        if (emoteSet.Emotes.Count == 0)
        {
            var knownActiveEmotes = await db.Emotes
                .CountAsync(e => e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);
            if (knownActiveEmotes > 0)
            {
                logger.LogWarning(
                    "7TV meldet 0 aktive Emotes für {Channel}, obwohl bisher {Count} bekannt waren — Sync übersprungen.",
                    normalized, knownActiveEmotes);
                return emoteSet.Id;
            }
        }

        channel.TwitchChannelId ??= twitchUserId;
        channel.ActiveEmoteSetId = emoteSet.Id;

        await ReconcileAsync(channel.Id, emoteSet.Emotes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        return emoteSet.Id;
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
