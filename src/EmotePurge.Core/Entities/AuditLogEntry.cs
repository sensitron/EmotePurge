namespace EmotePurge.Core.Entities;

/// <summary>
/// The vocabulary of <see cref="AuditLogEntry.Action"/>. Constants rather than an enum, because the
/// value is persisted and shipped to the frontend as-is: the strings are the contract (the UI maps
/// each one to an <c>admin.audit.actions.*</c> translation key), and an enum would have made the
/// stored representation an implementation detail that renumbering could silently break.
/// </summary>
public static class AuditActions
{
    public const string ChannelJoin = "channel.join";
    public const string ChannelLeave = "channel.leave";
    public const string ChannelPurge = "channel.purge";
    public const string VoteSessionCreate = "voteSession.create";
    public const string VoteSessionEnd = "voteSession.end";
    public const string VoteSessionDelete = "voteSession.delete";
    public const string EmotesSyncDeleted = "emotes.syncDeleted";
    public const string UserRevokeSessions = "user.revokeSessions";
    public const string ChannelResync = "channel.resync";
    public const string UserInvalidateRoleCache = "user.invalidateRoleCache";
}

/// <summary>
/// One recorded administrative action. Written by the service that performs the action, inside that
/// action's own <c>SaveChangesAsync</c> — so an entry exists if and only if the action committed.
/// <para>
/// Every reference to something else is a <b>snapshot string, never a foreign key</b>, and that is
/// deliberate on two counts. The actor is not an FK to <c>User</c>: <c>Vote.UserId</c> already uses
/// <c>DeleteBehavior.Restrict</c> to keep a future user deletion from wiping vote history, and a
/// second restricting edge from the audit log would make the audit trail itself the thing blocking
/// that deletion — an audit log must not become an obstacle to the operations it records. The
/// channel is not an FK either, because <c>channel.purge</c> deletes the channel row in the very
/// transaction that writes its audit entry; a cascade would delete the record of the deletion, and a
/// restrict would forbid the purge outright.
/// </para>
/// <para>
/// Retention is unbounded on purpose (see the decision log) — a cleanup job can be added later
/// without changing this shape.
/// </para>
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    // Snapshot of who acted, taken at action time — see the type remarks for why this is not an FK.
    public string ActorTwitchUserId { get; set; } = string.Empty;
    public string ActorLogin { get; set; } = string.Empty;

    // One of the AuditActions constants.
    public string Action { get; set; } = string.Empty;

    // Normalized channel name (ChannelName.Normalize), null for actions with no channel scope.
    public string? ChannelName { get; set; }

    // Optional pointer to the affected object, e.g. ("voteSession", "42").
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    // Action-specific payload as JSON (jsonb column), e.g. {"emoteCount":12} or {"title":"..."}.
    // Free-form on purpose: every action may carry what it needs without a schema change, and the
    // UI reads it defensively.
    public string? DetailsJson { get; set; }
}
