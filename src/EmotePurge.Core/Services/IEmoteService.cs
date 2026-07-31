namespace EmotePurge.Core.Services;

public record SyncDeletedResultDto(int ArchivedCount, IReadOnlyList<string> NotFoundIds);

public interface IEmoteService
{
    // Soft-archive (IsArchived=true), never a hard delete — see CLAUDE.md decision log on why
    // emote rows must survive a 7TV deletion (UsageStat/Vote history cascades off Emote.Id).
    // emoteIds not belonging to this channel, unknown, or already archived land in NotFoundIds
    // instead of failing the whole batch.
    // actor is audited (emotes.syncDeleted) together with the archiving, in the same transaction —
    // but only when something was actually archived; a call that matched nothing is not an event.
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);
}
