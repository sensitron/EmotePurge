using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Routing.Patterns;

namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// Counts what the rate limiter decided, from just outside it.
/// </summary>
/// <remarks>
/// <para>
/// Registered <b>before</b> <c>UseRateLimiter</c> and measuring <b>after</b> the inner pipeline has
/// returned, because both halves of the answer only exist by then: the partitioner leaves the policy
/// name and the partition behind on its way in, and <c>OnRejectedAsync</c> leaves its marker on the
/// way out. From inside the limiter neither is visible, and from a handler the limiter is invisible.
/// </para>
/// <para>
/// It counts requests, not responses. A 500, a 403 and a 200 all spent the same permit and are all
/// filed as accepted; the status code is not a dimension here, because the question this feeds is
/// "how much of the budget was used and how often did it run out", not "what did the endpoint
/// answer". The one status that would be misread is 429 — and telling a limiter 429 apart from a
/// handler's own is exactly what the marker exists for.
/// </para>
/// <para>
/// Reporting is fire-and-forget (see <see cref="RateLimitTelemetryExtensions"/>): a counter must not
/// add a Redis round trip to a request, and must not be able to fail one.
/// </para>
/// </remarks>
public sealed class RateLimitTelemetryMiddleware(RequestDelegate next, IRateLimitTelemetry telemetry)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        var decision = RateLimitRejection.TryDescribeDecision(context, ResolveRouteTemplate(context));
        if (decision is not null)
        {
            telemetry.RecordPolicyDecision(decision);
        }
    }

    /// <summary>
    /// The endpoint's route template, never the request path.
    /// </summary>
    /// <remarks>
    /// <c>/api/channels/{channelName}/permissions</c> is one row an operator can read. The raw paths
    /// would be one row per channel anyone ever opened — an unbounded key space in Redis, keyed by
    /// user-supplied text, in the one place whose entire job is to stay readable during an incident.
    /// A request carrying a policy always has a route endpoint (the limiter finds its policy on that
    /// endpoint's metadata), so the fallback below is unreachable in practice and deliberately still
    /// a constant rather than the path.
    /// </remarks>
    private static string ResolveRouteTemplate(HttpContext context)
        => context.GetEndpoint() is RouteEndpoint { RoutePattern: RoutePattern { RawText: { } rawText } }
            ? rawText
            : "unknown";
}
