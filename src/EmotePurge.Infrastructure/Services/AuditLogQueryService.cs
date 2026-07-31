using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AuditLogQueryService(AppDbContext db) : IAuditLogQueryService
{
    public async Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var totalCount = await db.AuditLogEntries.CountAsync(cancellationToken);

        var items = await db.AuditLogEntries
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAtUtc)
            // Id descending as the tiebreaker, not decoration: entries written inside one transaction
            // share a timestamp to the tick, and without a total order Skip/Take may return the same
            // row on two pages and drop another entirely.
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new AuditLogEntryDto(
                e.Id,
                e.OccurredAtUtc,
                e.ActorTwitchUserId,
                e.ActorLogin,
                e.Action,
                e.ChannelName,
                e.TargetType,
                e.TargetId,
                e.DetailsJson))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogEntryDto>(items, page, pageSize, totalCount);
    }
}
