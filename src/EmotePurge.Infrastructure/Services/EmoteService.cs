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

        var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
        if (channel is null)
        {
            return new SyncDeletedResultDto(0, emoteIds);
        }

        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && emoteIds.Contains(e.Id) && !e.IsArchived)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var emote in emotes)
        {
            emote.IsArchived = true;
            emote.LastSyncedAt = now;
        }

        // Nothing matched means nothing was archived — a re-sent batch after a retry, or ids from
        // another channel. That is not an event worth a row; the caller still gets its NotFoundIds.
        if (emotes.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncDeleted,
                channelName: normalized,
                details: new { emoteCount = emotes.Count });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundIds = emotes.Select(e => e.Id).ToHashSet();
        var notFoundIds = emoteIds.Where(id => !foundIds.Contains(id)).ToList();

        return new SyncDeletedResultDto(emotes.Count, notFoundIds);
    }
}
