using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EmotePurge.Api.Validation;
using Microsoft.AspNetCore.RateLimiting;

namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// The partitioning and the rejected answer of every rate-limit policy, in one place because they
/// share a secret: only the partitioner knows how the partition key was derived, and only the
/// rejection needs to name it.
/// </summary>
/// <remarks>
/// Before 2026-08-29 there was no rejection handler at all. A throttled request got a bare 429 — no
/// body, no Retry-After, no log line — so the frontend fell back to its generic status message and
/// the server side of the story simply did not exist. Two issues (#33, #35) had to be traced through
/// the production nginx access log to establish that the 429s were ours and not Cloudflare's.
/// </remarks>
internal static class RateLimitRejection
{
    /// <summary>
    /// Category of the rejection log. A constant with a stable, explicit name rather than
    /// <c>ILogger&lt;Program&gt;</c>, because log aggregation alerts on it (module E) and a category
    /// derived from a type would move the moment the type does.
    /// </summary>
    internal const string LogCategory = "EmotePurge.Api.RateLimiting";

    /// <summary>
    /// The window of every policy — they differ only in permit count. Also the Retry-After fallback:
    /// the full window is never too short, so a client that waits it out is always past the boundary.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string PolicyItemKey = "RateLimit:Policy";
    private const string PartitionItemKey = "RateLimit:PartitionKey";

    /// <summary>
    /// Partitions by the authenticated Twitch user, falling back to the remote IP and finally to a
    /// shared bucket. Runs after UseAuthentication, so the claim is there for every endpoint that
    /// requires auth.
    /// </summary>
    /// <remarks>
    /// Records the policy name and the partition key on the request. <see cref="OnRejectedAsync"/>
    /// has no other way to name either: the middleware hands it a lease, not a policy, and
    /// re-deriving the key there would duplicate the fallback chain above — two copies that can
    /// drift, in the one place whose whole job is to say accurately what happened.
    /// </remarks>
    public static RateLimitPartition<string> PartitionPerUser(
        HttpContext httpContext,
        string policyName,
        int permitLimit)
    {
        var partitionKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        httpContext.Items[PolicyItemKey] = policyName;
        httpContext.Items[PartitionItemKey] = partitionKey;

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0
        });
    }

    /// <summary>
    /// Answers a rejected request: one warning in the log, a Retry-After header, and a body carrying
    /// a translatable error code.
    /// </summary>
    /// <remarks>
    /// The status code is already 429 when this runs — <c>RateLimiterOptions.RejectionStatusCode</c>
    /// is applied *before* the callback, which is why writing a body here is safe and why nothing
    /// below sets a status.
    /// </remarks>
    public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var policyName = httpContext.Items[PolicyItemKey] as string ?? "unknown";
        var partitionKey = httpContext.Items[PartitionItemKey] as string ?? "unknown";

        // FixedWindowRateLimiter reports the wait to the next window boundary on a failed lease. On
        // .NET 10 that value is the whole window rather than the remainder — an over-estimate, and
        // the safe direction to be wrong in; .NET 11 makes it exact with no change here.
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : (int)Window.TotalSeconds;

        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        // The partition key is a Twitch user id for every authenticated caller and the remote IP only
        // for the anonymous health endpoint — both are already in the database respectively in the
        // reverse proxy's own access log, so this adds no category of data the host did not hold.
        var logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory);
        logger.LogWarning(
            "Rate-Limit erreicht: Policy {RateLimitPolicy}, {RequestMethod} {RequestPath}, Partition {RateLimitPartition}, Retry-After {RetryAfterSeconds}s",
            policyName,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            partitionKey,
            retryAfterSeconds);

        // Shaped like the resync cooldown's 429 (ChannelEndpoints.cs), so the frontend's existing
        // apiErrorTranslationKey handles both without a special case.
        await httpContext.Response.WriteAsJsonAsync(
            new { errorCode = ApiErrorCodes.RateLimitExceeded, retryAfterSeconds },
            cancellationToken);
    }
}
