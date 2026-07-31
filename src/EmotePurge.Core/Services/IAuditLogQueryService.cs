namespace EmotePurge.Core.Services;

/// <summary>
/// One audit-log row as the admin UI receives it — a straight projection of
/// <see cref="Entities.AuditLogEntry"/>, including <paramref name="Id"/> so the client has a stable
/// list key. <paramref name="Action"/> is one of the <see cref="Entities.AuditActions"/> constants
/// and stays language-neutral; the frontend owns the wording.
/// </summary>
public record AuditLogEntryDto(
    long Id,
    DateTime OccurredAtUtc,
    string ActorTwitchUserId,
    string ActorLogin,
    string Action,
    string? ChannelName,
    string? TargetType,
    string? TargetId,
    string? DetailsJson);

/// <summary>
/// Read side of the audit log, behind GET /api/admin/audit-log. Separate from the services that
/// write entries: those own one action each and add their entry to the action's own transaction,
/// while this one only ever reads.
/// </summary>
public interface IAuditLogQueryService
{
    /// <summary>
    /// Newest first. <paramref name="page"/> is 1-based; both arguments are expected to be
    /// pre-validated by the endpoint, same as the other paged query services.
    /// </summary>
    Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}
