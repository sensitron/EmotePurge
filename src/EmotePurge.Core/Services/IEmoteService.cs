namespace EmotePurge.Core.Services;

public record SyncDeletedResultDto(int ArchivedCount, IReadOnlyList<string> NotFoundIds);

public interface IEmoteService
{
    // Soft-archive (IsArchived=true), never a hard delete — see CLAUDE.md decision log on why
    // emote rows must survive a 7TV deletion (UsageStat/Vote history cascades off Emote.Id).
    // emoteIds not belonging to this channel, unknown, or already archived land in NotFoundIds
    // instead of failing the whole batch.
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, CancellationToken cancellationToken = default);
}
