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
    IDuplicateEmoteNameTracker duplicateNameTracker,
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

        var twitchUserId = channel.TwitchChannelId;
        if (twitchUserId is null)
        {
            var resolved = await sevenTvApiClient.ResolveTwitchUserIdAsync(normalized, cancellationToken);
            if (resolved.Status != SevenTvLookupStatus.Ok || resolved.TwitchUserId is null)
            {
                await RecordFailedAttemptAsync(channel, resolved.Status, cancellationToken);
                return null;
            }

            twitchUserId = resolved.TwitchUserId;

            // A rename leaves this exact shape: a second row under the new name, still without its
            // own TwitchChannelId, resolving to the Twitch account the original row already holds.
            // Writing it here via the backfill below would collide with the unique index on
            // Channel.TwitchChannelId — so this row is left untouched. Reconciling the duplicate
            // (folding it into the original, or vice versa) is not this method's job.
            var existingOwner = await db.LoadChannelByTwitchIdAsync(twitchUserId, cancellationToken);
            if (existingOwner is not null && existingOwner.Id != channel.Id)
            {
                logger.LogWarning(
                    "SyncChannelAsync: {Channel} ({ChannelId}) löst dieselbe Twitch-ID {TwitchId} auf wie bereits getrackter Channel {ExistingChannel} ({ExistingChannelId}) — vermutlich ein Rename-Duplikat, Sync übersprungen.",
                    normalized, channel.Id, twitchUserId, existingOwner.ChannelName, existingOwner.Id);
                return null;
            }
        }

        var channelState = await sevenTvApiClient.GetChannelStateForTwitchUserAsync(twitchUserId, cancellationToken);
        if (channelState.Status != SevenTvLookupStatus.Ok || channelState.State is null)
        {
            await RecordFailedAttemptAsync(channel, channelState.Status, cancellationToken);
            return null;
        }

        var emoteSet = channelState.State.EmoteSet;

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
                return new SevenTvSyncResult(emoteSet.Id, channelState.State.SevenTvUserId, HasChanges: false);
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
        // Unconditional, and deliberately not part of the change bookkeeping below: "we successfully
        // reached 7TV and reconciled" is true even when nothing moved, and that is precisely what
        // makes it worth recording separately from the inventory timestamps. Written only here, never
        // in the delta path — a dispatch is not a full reconciliation, and ApplyEmoteSetUpdateAsync
        // decides NoChange vs Applied by asking the ChangeTracker, so a write there would turn every
        // no-op dispatch into a live event.
        var syncedAt = DateTime.UtcNow;
        channel.LastSyncedAtUtc = syncedAt;
        // The reset half of the contract, and the one that gets forgotten: a channel that activated
        // an emote set on 7TV must stop being told it has none. Written in the same block as the
        // success stamp so the two cannot drift apart — a reason cleared anywhere else would need a
        // second place to remember it.
        channel.LastSyncAttemptAtUtc = syncedAt;
        channel.LastSyncFailureReason = null;

        var inventoryChanged = await ReconcileAsync(channel.Id, emoteSet.Emotes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        return new SevenTvSyncResult(emoteSet.Id, channelState.State.SevenTvUserId, emoteSetSwitched || inventoryChanged);
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
            // A push IS the moment the emote enters the set, so "now" is the true added-at — the
            // one place in the delta path where the date is known without asking v4.
            UpsertEmote(channel.Id, existing, emote with { AddedToSetAt = DateTime.UtcNow }, fromDispatch: true);
        }

        foreach (var emote in delta.Updated)
        {
            UpsertEmote(channel.Id, existing, emote, fromDispatch: true);
        }

        foreach (var pulledId in delta.PulledIds)
        {
            if (existing.TryGetValue(pulledId, out var emote) && !emote.IsArchived)
            {
                emote.IsArchived = true;
                emote.ArchivedAt = DateTime.UtcNow;
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

    /// <summary>
    /// Records why an attempt produced nothing. Writes the reason and the attempt timestamp and
    /// nothing else — deliberately not <c>ActiveEmoteSetId</c>, the capacity or any emote row: a
    /// 7TV outage must not take the mass-delete panel away or archive a whole set, and
    /// <c>LastSyncedAtUtc</c> keeps meaning "last *successful* sync".
    /// </summary>
    private async Task RecordFailedAttemptAsync(Channel channel, SevenTvLookupStatus status, CancellationToken cancellationToken)
    {
        var reason = SevenTvSyncFailureReasons.FromStatus(status);
        // Logged only when the reason changes: the periodic resync runs this for every broken
        // channel every 60 seconds, and an unconditional line would bury everything else in the log.
        // The stored value is what the UI reads, so nothing is lost by staying quiet.
        var changed = channel.LastSyncFailureReason != reason;

        channel.LastSyncAttemptAtUtc = DateTime.UtcNow;
        channel.LastSyncFailureReason = reason;
        await db.SaveChangesAsync(cancellationToken);

        if (changed)
        {
            logger.LogInformation(
                "7TV-Sync für {Channel} ohne Ergebnis: {Reason}.", channel.ChannelName, reason);
        }
    }

    private async Task RefreshMatchCacheAsync(Channel channel, CancellationToken cancellationToken)
    {
        var activeEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
            .Select(e => new { e.Name, e.Id })
            .ToListAsync(cancellationToken);

        // 7TV active sets can legitimately contain two emotes sharing the same chat alias
        // (observed live) — ToDictionary would throw, so duplicates are coalesced instead,
        // keeping whichever was loaded first. Logged only when the collision set changes: this
        // method runs on every resync tick, and a static collision would spam the log otherwise.
        // The full current state is served by IDuplicateEmoteNameQueryService instead.
        var emoteNameToId = new Dictionary<string, string>();
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emote in activeEmotes)
        {
            if (!emoteNameToId.TryAdd(emote.Name, emote.Id))
            {
                duplicateNames.Add(emote.Name);
            }
        }

        if (duplicateNameTracker.Update(channel.ChannelName, duplicateNames))
        {
            if (duplicateNames.Count > 0)
            {
                logger.LogWarning(
                    "{Count} doppelte aktive Emote-Namen in Channel {Channel}: {Names} — Chat-Matching zählt je Name nur auf die zuerst geladene Emote-Id.",
                    duplicateNames.Count, channel.ChannelName,
                    string.Join(", ", duplicateNames.Order(StringComparer.Ordinal)));
            }
            else
            {
                logger.LogInformation(
                    "Namenskollisionen in Channel {Channel} aufgelöst — alle aktiven Emote-Namen sind wieder eindeutig.",
                    channel.ChannelName);
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
                emote.ArchivedAt = DateTime.UtcNow;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Returns true when the row was created or actually modified.
    /// <para>
    /// <paramref name="fromDispatch"/> marks the EventAPI delta path, whose payloads are less
    /// complete and less trusted than a REST answer. Two behaviours hang off it, both about not
    /// letting a thin payload destroy known state.
    /// </para>
    /// </summary>
    private bool UpsertEmote(string channelId, Dictionary<string, Emote> existing, SevenTvEmote live, bool fromDispatch = false)
    {
        if (existing.TryGetValue(live.Id, out var emote))
        {
            // Correction, not write-once backfill: the column was originally filled from the v3
            // payload's timestamp, which turned out to be the emote's upload date rather than the
            // set-entry date — so a known value may be known-wrong, and the v4-sourced answer must
            // win. It also moves a re-added emote's date forward to its latest set entry. Kept
            // outside the change detection below: learning *when* an emote joined the set is not an
            // inventory change; counted as one, the first resync after a deploy would fire
            // channel.synced for every channel at once and make every open page refetch.
            //
            // REST only, because ApplyEmoteSetUpdateAsync decides NoChange vs Applied by asking the
            // ChangeTracker — a correction there would turn a no-op dispatch into a live event. The
            // periodic resync fills the same gap within a tick, so nothing is lost by waiting.
            // Null never overwrites: losing a known date to a failed v4 lookup would reopen the gap.
            if (!fromDispatch && live.AddedToSetAt is not null && emote.FirstSeenAt != live.AddedToSetAt)
            {
                emote.FirstSeenAt = live.AddedToSetAt;
            }

            // Dispatch payloads have not been proven to always carry the image-host block, so the
            // delta path never overwrites a known image URL with an empty one (the REST path keeps
            // its verbatim behaviour — there an empty URL is 7TV's authoritative answer).
            var imageUrl = fromDispatch && live.ImageUrl.Length == 0 && emote.ImageUrl.Length > 0
                ? emote.ImageUrl
                : live.ImageUrl;

            if (emote.Name != live.Name || emote.ImageUrl != imageUrl || emote.IsArchived)
            {
                emote.Name = live.Name;
                emote.ImageUrl = imageUrl;
                emote.IsArchived = false;
                // Inside the change block, not part of the condition: clearing an archive date is
                // only meaningful on a real un-archive, and putting it into the condition would turn
                // no-op rows into inventory changes that fire channel.synced for every open page.
                emote.ArchivedAt = null;
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
            ImageUrl = live.ImageUrl,
            FirstSeenAt = live.AddedToSetAt
        };
        db.Emotes.Add(emote);
        existing[live.Id] = emote;
        return true;
    }
}
