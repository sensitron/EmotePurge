namespace EmotePurge.Core.Services;

/// <summary>
/// Who is performing an audited action. Passed down from the endpoint (built from the authenticated
/// principal) into the service that writes the audit entry, rather than resolved inside
/// Infrastructure: <c>EmotePurge.Core</c> and <c>EmotePurge.Infrastructure</c> know nothing about
/// ASP.NET Core's <c>HttpContext</c>, and threading the actor through as a plain parameter keeps the
/// services callable from a test or the worker without an HTTP request in scope.
/// </summary>
public record AuditActor(string TwitchUserId, string Login)
{
    /// <summary>
    /// The actor for worker-driven actions no user triggered — the periodic channel-identity
    /// reconciliation being the first of them. Both fields carry the literal <c>system</c>, so the
    /// admin audit log renders it as the login <c>system</c> with no special case of its own: the
    /// actor is a snapshot string there, never a foreign key into <c>User</c> (see
    /// <see cref="EmotePurge.Core.Entities.AuditLogEntry"/>), and nothing downstream tries to
    /// resolve it to an account.
    /// </summary>
    public static AuditActor System { get; } = new("system", "system");
}
