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
/// Optional narrowing of the audit-log list; every field is AND-combined, null means "no filter".
/// <paramref name="Action"/> matches exactly against the <see cref="Entities.AuditActions"/>
/// constants (an unknown value yields an empty page rather than an error).
/// <paramref name="ChannelName"/> matches exactly on the normalized form — callers may pass raw
/// user input, the implementation normalizes via <see cref="Entities.ChannelName.Normalize"/>.
/// <paramref name="ActorLogin"/> is a case-insensitive substring match, because actor logins are
/// not enumerable in the UI the way actions and channels are.
/// </summary>
public record AuditLogFilter(string? Action, string? ChannelName, string? ActorLogin);

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
    /// <paramref name="filter"/> narrows the result; the page count reflects the filtered set.
    /// </summary>
    Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, AuditLogFilter? filter = null, CancellationToken cancellationToken = default);
}
