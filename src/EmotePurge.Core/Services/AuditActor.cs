namespace EmotePurge.Core.Services;

/// <summary>
/// Who is performing an audited action. Passed down from the endpoint (built from the authenticated
/// principal) into the service that writes the audit entry, rather than resolved inside
/// Infrastructure: <c>EmotePurge.Core</c> and <c>EmotePurge.Infrastructure</c> know nothing about
/// ASP.NET Core's <c>HttpContext</c>, and threading the actor through as a plain parameter keeps the
/// services callable from a test or the worker without an HTTP request in scope.
/// </summary>
public record AuditActor(string TwitchUserId, string Login);
