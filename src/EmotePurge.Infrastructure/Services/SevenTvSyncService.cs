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
    public async Task<SevenTvSyncResult?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        // Two concurrent syncs of the same channel collide on the (ChannelId, SevenTvEmoteId)
        // unique index — see ChannelSyncGate.
        using var gate = await channelSyncGate.AcquireAsync(normalized, cancellationToken);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
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

        var channelState = await sevenTvApiClient.GetChannelStateForTwitchUserAsync(twitchUserId, cancellationToken);
        if (channelState is null)
        {
            return null;
        }

        var emoteSet = channelState.EmoteSet;

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
                return new SevenTvSyncResult(emoteSet.Id, channelState.SevenTvUserId, HasChanges: false);
            }
        }

        // Read before the assignment below: a switched active set is a content change of its own,
        // even when the two sets happen to hold identical emotes. A first-time TwitchChannelId
        // backfill deliberately does not count — it changes no emote the UI could show.
        var emoteSetSwitched = channel.ActiveEmoteSetId != emoteSet.Id;

        channel.TwitchChannelId ??= twitchUserId;
        channel.ActiveEmoteSetId = emoteSet.Id;
        // Written here and only here, in lockstep with the set id: the EventAPI delta path carries no
        // capacity, so writing it there would leave a switched set showing the old set's limit until
        // the next resync. Deliberately outside the inventory-change bookkeeping below — a changed
        // capacity is not a changed emote and must not make every open page refetch.
        channel.ActiveEmoteSetCapacity = emoteSet.Capacity;

        var inventoryChanged = await ReconcileAsync(channel.Id, emoteSet.Emotes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        return new SevenTvSyncResult(emoteSet.Id, channelState.SevenTvUserId, emoteSetSwitched || inventoryChanged);
    }

    public async Task<SevenTvDeltaOutcome> ApplyEmoteSetUpdateAsync(
        string channelName,
        string emoteSetId,
        SevenTvEmoteSetDelta delta,
        CancellationToken cancellationToken = default)
    {
        if (delta.IsEmpty)
        {
            return SevenTvDeltaOutcome.NoChange;
        }

        var normalized = ChannelName.Normalize(channelName);
        using var gate = await channelSyncGate.AcquireAsync(normalized, cancellationToken);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            logger.LogWarning("ApplyEmoteSetUpdateAsync: {Channel} nicht in Postgres gefunden.", normalized);
            return SevenTvDeltaOutcome.ChannelUnknown;
        }

        if (channel.ActiveEmoteSetId != emoteSetId)
        {
            logger.LogInformation(
                "7TV-Dispatch für Set {SetId} ignoriert — {Channel} hat inzwischen Set {ActiveSetId} aktiv.",
                emoteSetId, normalized, channel.ActiveEmoteSetId);
            return SevenTvDeltaOutcome.SetNotActive;
        }

        var existing = await db.Emotes
            .Where(e => e.ChannelId == channel.Id)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        // Same asymmetry as the empty-set guard in SyncChannelAsync: one malformed or malicious
        // delta that would archive the channel's entire active set kills chat matching until the
        // next resync. Simulated first, and skipped when the result would be a wipe — the caller
        // reacts with a full resync, which re-checks against 7TV's authoritative state.
        var activeBefore = existing.Values.Count(e => !e.IsArchived);
        var activeAfter = existing.Values
            .Where(e => !e.IsArchived)
            .Select(e => e.SevenTvEmoteId)
            .ToHashSet();
        foreach (var emote in delta.Pushed)
        {
            activeAfter.Add(emote.Id);
        }
        foreach (var emote in delta.Updated)
        {
            activeAfter.Add(emote.Id);
        }
        activeAfter.ExceptWith(delta.PulledIds);

        if (activeBefore > 0 && activeAfter.Count == 0)
        {
            logger.LogWarning(
                "7TV-Dispatch würde alle {Count} aktiven Emotes von {Channel} entfernen — als unplausibel übersprungen.",
                activeBefore, normalized);
            return SevenTvDeltaOutcome.ImplausibleSkipped;
        }

        foreach (var emote in delta.Pushed)
        {
            UpsertEmote(channel.Id, existing, emote, preserveImageUrlWhenLiveEmpty: true);
        }

        foreach (var emote in delta.Updated)
        {
            UpsertEmote(channel.Id, existing, emote, preserveImageUrlWhenLiveEmpty: true);
        }

        foreach (var pulledId in delta.PulledIds)
        {
            if (existing.TryGetValue(pulledId, out var emote) && !emote.IsArchived)
            {
                emote.IsArchived = true;
                emote.LastSyncedAt = DateTime.UtcNow;
            }
        }

        if (!db.ChangeTracker.HasChanges())
        {
            return SevenTvDeltaOutcome.NoChange;
        }

        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        logger.LogInformation(
            "7TV-Dispatch auf {Channel} angewendet: {Pushed} hinzugefügt, {Updated} aktualisiert, {Pulled} entfernt.",
            normalized, delta.Pushed.Count, delta.Updated.Count, delta.PulledIds.Count);
        return SevenTvDeltaOutcome.Applied;
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

    /// <summary>Returns true when at least one emote row was added, archived or altered.</summary>
    private async Task<bool> ReconcileAsync(string channelId, IReadOnlyList<SevenTvEmote> liveEmotes, CancellationToken cancellationToken)
    {
        var existing = await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        var liveIds = liveEmotes.Select(e => e.Id).ToHashSet();
        var changed = false;

        foreach (var emote in liveEmotes)
        {
            changed |= UpsertEmote(channelId, existing, emote);
        }

        foreach (var (sevenTvEmoteId, emote) in existing)
        {
            if (!liveIds.Contains(sevenTvEmoteId) && !emote.IsArchived)
            {
                // Measurement only (no protection): 7TV's REST cache can lag 10-30 min behind
                // (SevenTV/SevenTV#81), so a resync may archive an emote a live dispatch added
                // moments ago — and dispatches never repeat. Logged to quantify how often that
                // actually happens before deciding whether a guard is worth weakening the
                // reconciliation for.
                if (emote.LastSyncedAt > DateTime.UtcNow.AddMinutes(-15))
                {
                    logger.LogInformation(
                        "REST-Resync archiviert Emote {Name} ({SevenTvEmoteId}), das vor weniger als 15 Minuten synchronisiert wurde — möglicher 7TV-REST-Cache-Lag.",
                        emote.Name, sevenTvEmoteId);
                }

                emote.IsArchived = true;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Returns true when the row was created or actually modified.</summary>
    private bool UpsertEmote(string channelId, Dictionary<string, Emote> existing, SevenTvEmote live, bool preserveImageUrlWhenLiveEmpty = false)
    {
        if (existing.TryGetValue(live.Id, out var emote))
        {
            // Dispatch payloads have not been proven to always carry the image-host block, so the
            // delta path never overwrites a known image URL with an empty one (the REST path keeps
            // its verbatim behaviour — there an empty URL is 7TV's authoritative answer).
            var imageUrl = preserveImageUrlWhenLiveEmpty && live.ImageUrl.Length == 0 && emote.ImageUrl.Length > 0
                ? emote.ImageUrl
                : live.ImageUrl;

            if (emote.Name != live.Name || emote.ImageUrl != imageUrl || emote.IsArchived)
            {
                emote.Name = live.Name;
                emote.ImageUrl = imageUrl;
                emote.IsArchived = false;
                emote.LastSyncedAt = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        emote = new Emote
        {
            ChannelId = channelId,
            SevenTvEmoteId = live.Id,
            Name = live.Name,
            ImageUrl = live.ImageUrl
        };
        db.Emotes.Add(emote);
        existing[live.Id] = emote;
        return true;
    }
}
