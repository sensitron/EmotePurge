namespace EmotePurge.Core.Services;

// ArchivedCount is the idempotent "goal state reached" count (already-archived rows included) that
// the caller reports back to the user; NewlyArchivedCount is the subset this call actually wrote —
// the only thing that may trigger a channel.synced live event. Not part of the HTTP response.
public record SyncDeletedResultDto(int ArchivedCount, IReadOnlyList<string> NotFoundIds, int NewlyArchivedCount);

// Mirror of SyncDeletedResultDto for the restore direction: RestoredCount is the goal-state count,
// NewlyRestoredCount the subset this call actually un-archived.
public record SyncRestoredResultDto(int RestoredCount, IReadOnlyList<string> NotFoundIds, int NewlyRestoredCount);

public interface IEmoteService
{
    // Soft-archive (IsArchived=true), never a hard delete — see CLAUDE.md decision log on why
    // emote rows must survive a 7TV deletion (UsageStat/Vote history cascades off Emote.Id).
    // Idempotent: an already-archived emote counts into ArchivedCount (the goal state is reached —
    // with the EventAPI live sync enabled, the worker routinely archives the emote before this
    // bookkeeping call arrives). Only ids unknown or belonging to another channel land in
    // NotFoundIds instead of failing the whole batch.
    // actor is audited (emotes.syncDeleted) together with the archiving, in the same transaction —
    // whenever the goal-state count is > 0, not only when this call changed rows: the user's delete
    // on 7TV happened either way, and gating the paper trail on winning the race against the live
    // sync made most real deletes invisible in the audit log.
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // The restore counterpart (A6 in-app restore): un-archives (IsArchived=false, ArchivedAt=null)
    // the given emotes after the browser re-added them to the 7TV set. Same idempotence and audit
    // semantics as MarkDeletedAsync, with emotes.syncRestored as the audit action.
    Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);
}
