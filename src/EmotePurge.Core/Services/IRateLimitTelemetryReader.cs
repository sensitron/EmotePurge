namespace EmotePurge.Core.Services;

/// <summary>
/// The read side of the rate-limit telemetry, for the global-admin monitoring page.
/// </summary>
/// <remarks>
/// Read-only by design: there is no write endpoint, no reservation and no observe/enforce switch in
/// this round. The counters describe what happened, they never feed a decision.
/// </remarks>
public interface IRateLimitTelemetryReader
{
    /// <summary>
    /// The complete counter snapshot. Never throws and never returns <c>null</c>: if the counter store
    /// is unreachable, the result is <see cref="RateLimitTelemetrySnapshot.Unavailable"/> so the admin
    /// endpoint can still answer 200 with its effective configuration.
    /// </summary>
    Task<RateLimitTelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything the counter store knows, at one instant.
/// </summary>
/// <param name="TelemetryAvailable">
/// <c>false</c> when the counters could not be read at all. The endpoint then degrades partially
/// instead of failing: the effective policy configuration comes from options and is unaffected.
/// </param>
/// <param name="Policies">Only policies that were used inside the retained window appear here.</param>
public record RateLimitTelemetrySnapshot(
    bool TelemetryAvailable,
    IReadOnlyList<RateLimitPolicyCounters> Policies,
    RateLimitLastRejection? LastLocalRejection,
    IReadOnlyList<RateLimitCacheCounters> Caches,
    IReadOnlyList<RateLimitProviderCounters> Providers)
{
    /// <summary>The one answer given when the counter store cannot be reached — empty, never an error.</summary>
    public static RateLimitTelemetrySnapshot Unavailable { get; } = new(false, [], null, [], []);
}

/// <summary>
/// Accepted and locally rejected requests of one policy, in both windows.
/// </summary>
/// <remarks>
/// Both windows are summed from the same events, so the minute count is always contained in the
/// 24-hour count. The minute is aligned to the store's small time buckets and therefore covers
/// slightly less than 60 seconds at any given moment; it moves in bucket-sized steps rather than
/// jumping when a window boundary passes.
/// </remarks>
public record RateLimitPolicyCounters(
    string PolicyName,
    long AcceptedLastMinute,
    long RejectedLastMinute,
    long AcceptedLast24Hours,
    long RejectedLast24Hours);

/// <summary>
/// The most recent rejection produced by a local policy, across all policies. One entry, overwritten
/// each time: the counters answer "how much", this answers "what exactly, last time".
/// </summary>
public record RateLimitLastRejection(
    DateTime ObservedAtUtc,
    string HttpMethod,
    string RouteTemplate,
    string PolicyName,
    string Partition,
    int? RetryAfterSeconds);

/// <summary>Hits and misses of one server-side cache, in both windows.</summary>
public record RateLimitCacheCounters(
    string CacheName,
    long HitsLastMinute,
    long MissesLastMinute,
    long HitsLast24Hours,
    long MissesLast24Hours);

/// <summary>
/// What one provider client did and what came back. Deliberately without a percentage: for 7TV there
/// is no defensible denominator, and reporting one for Twitch only would invite reading it as a budget.
/// </summary>
/// <param name="RateLimitedLastMinute">Real provider 429s — never a local policy rejection.</param>
/// <param name="LastHeaderSample">
/// The provider's own rate-limit headers as last seen. A sample for an operator to look at, explicitly
/// not authoritative and not shared state.
/// </param>
public record RateLimitProviderCounters(
    string ProviderName,
    string CallSource,
    long RequestsLastMinute,
    long RequestsLast24Hours,
    long RateLimitedLastMinute,
    long RateLimitedLast24Hours,
    int? LastRetryAfterSeconds,
    DateTime? LastRateLimitedAtUtc,
    ProviderRateLimitHeaderSample? LastHeaderSample);

/// <summary>
/// The <c>Ratelimit-*</c> headers of one response, kept as the strings they arrived as. Not parsed,
/// because nothing in this round is allowed to act on them.
/// </summary>
public record ProviderRateLimitHeaderSample(
    DateTime ObservedAtUtc,
    string? Limit,
    string? Remaining,
    string? Reset);
