using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AuditLogQueryService(AppDbContext db) : IAuditLogQueryService
{
    public async Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, AuditLogFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var query = ApplyFilter(db.AuditLogEntries.AsNoTracking(), filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
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

    private static IQueryable<AuditLogEntry> ApplyFilter(IQueryable<AuditLogEntry> query, AuditLogFilter? filter)
    {
        if (filter is null)
        {
            return query;
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.ChannelName))
        {
            // Exact match on the normalized form (Regel 9) — this is what the
            // (ChannelName, OccurredAtUtc) index serves, unlike a substring scan.
            var normalized = ChannelName.Normalize(filter.ChannelName);
            query = query.Where(e => e.ChannelName == normalized);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActorLogin))
        {
            // Substring on purpose: logins are free text to the admin. ILIKE cannot use the
            // btree index, but the actor-filtered set is small enough that this is fine.
            var pattern = $"%{filter.ActorLogin.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.ActorLogin, pattern));
        }

        return query;
    }
}
