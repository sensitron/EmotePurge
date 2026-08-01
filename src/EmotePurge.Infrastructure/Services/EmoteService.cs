using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteService(AppDbContext db) : IEmoteService
{
    public async Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncDeletedResultDto(0, emoteIds, 0);
        }

        // Already-archived rows are matched on purpose: with the EventAPI live sync enabled, the
        // worker usually archives the emote off the 7TV dispatch before this bookkeeping call
        // arrives. The goal state is reached either way, so both count as archived — reporting
        // them as "not found" made every successful delete look like a failed sync in the UI.
        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && emoteIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        var newlyArchived = emotes.Where(e => !e.IsArchived).ToList();
        var now = DateTime.UtcNow;
        foreach (var emote in newlyArchived)
        {
            emote.IsArchived = true;
            emote.LastSyncedAt = now;
        }

        // No state change (all already archived by the live sync, or ids from another channel) is
        // not an event worth an audit row; the caller still gets its counts.
        if (newlyArchived.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncDeleted,
                channelName: normalized,
                details: new { emoteCount = newlyArchived.Count });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundIds = emotes.Select(e => e.Id).ToHashSet();
        var notFoundIds = emoteIds.Where(id => !foundIds.Contains(id)).ToList();

        return new SyncDeletedResultDto(emotes.Count, notFoundIds, newlyArchived.Count);
    }
}
