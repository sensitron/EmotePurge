namespace EmotePurge.Core.Services;

/// <summary>
/// Reads a channel's active 7TV emote set for a caller who has no role there at all — the read side
/// of "Fremde Kanäle als Import-Quelle" (spec 2026-09-09). Every step is read-only: nothing is
/// written to our database, no channel is joined, no worker code runs, and this is deliberately not
/// the group's <c>UsageStatsAccessAuthorizationFilter</c> path — any logged-in user may call this for
/// any Twitch channel.
/// </summary>
/// <remarks>
/// A thin seam by design. Hardening — the 60 s Redis cache on the assembled response, request
/// coalescing, the 429-aware circuit breaker, and the provider-wide concurrency budget (spec section
/// 6, E4, E5) — is a separate task that wraps this interface (or the two collaborators behind its
/// implementation) with a decorator. The base implementation stays exactly what its name says: the
/// three-step resolution chain (F1) plus the paginated set read (F3), nothing else.
/// </remarks>
public interface IForeignEmoteSetService
{
    Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetAsync(string channelName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every way <see cref="IForeignEmoteSetService.GetForeignEmoteSetAsync"/> can end, matching the
/// state table in section 5 of the spec one-to-one — with one addition that never reaches the wire:
/// see <see cref="SevenTvRateLimited"/>.
/// </summary>
public enum ForeignEmoteSetLookupStatus
{
    Ok,
    ChannelNotOnTwitch,
    TwitchUnavailable,
    NoSevenTvAccount,
    NoActiveEmoteSet,
    SevenTvUnavailable,

    /// <summary>
    /// A confirmed 7TV overload (HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>) rather
    /// than a generic upstream failure. Kept apart from <see cref="SevenTvUnavailable"/> only so a
    /// hardening decorator (T2's circuit breaker, spec E4) can react differently — open immediately
    /// and honor a <c>Retry-After</c>, instead of counting toward the five-failure threshold. The API
    /// endpoint maps both statuses to the same 503 error code: the spec's state table has a single
    /// row for "7TV nicht erreichbar / 429", so this distinction is invisible on the wire.
    /// </summary>
    SevenTvRateLimited
}

/// <summary>
/// <see cref="EmoteSet"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="ForeignEmoteSetLookupStatus.Ok"/>, and the two factories are the only way to build one
/// — the same invariant-by-construction pattern <c>TwitchUserLookup</c> and the <c>SevenTv*Result</c>
/// family already use, for the same reason: a "found" result without a payload is exactly the value
/// every caller of this type assumes cannot exist.
/// </summary>
public sealed class ForeignEmoteSetLookupResult
{
    private ForeignEmoteSetLookupResult(ForeignEmoteSetLookupStatus status, ForeignEmoteSet? emoteSet)
    {
        Status = status;
        EmoteSet = emoteSet;
    }

    public ForeignEmoteSetLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="ForeignEmoteSetLookupStatus.Ok"/>.</summary>
    public ForeignEmoteSet? EmoteSet { get; }

    public static ForeignEmoteSetLookupResult Ok(ForeignEmoteSet emoteSet)
    {
        ArgumentNullException.ThrowIfNull(emoteSet);
        return new ForeignEmoteSetLookupResult(ForeignEmoteSetLookupStatus.Ok, emoteSet);
    }

    public static ForeignEmoteSetLookupResult Failed(ForeignEmoteSetLookupStatus status)
    {
        if (status == ForeignEmoteSetLookupStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Failed() kann keinen Erfolgsstatus tragen — für Ok ist Ok(emoteSet) zuständig.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unbekannter ForeignEmoteSetLookupStatus.");
        }

        return new ForeignEmoteSetLookupResult(status, null);
    }
}

/// <summary>
/// The successful answer, shaped to match <c>ForeignEmoteSetResponse</c> from the spec (section 4)
/// property for property — <c>System.Text.Json</c>'s default camelCase policy is the only mapping
/// step the API endpoint needs, the same convention <c>ResyncCooldownState</c> and
/// <c>SevenTvChannelState</c> already rely on elsewhere in this codebase.
/// </summary>
public sealed record ForeignEmoteSet(
    string ChannelName,
    string SevenTvUserId,
    string EmoteSetId,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<ForeignEmoteRow> Emotes);

/// <summary>
/// One emote in a foreign set preview. <see cref="SevenTvEmoteId"/> is 7TV's own ObjectID, never our
/// internal <c>Emote.Id</c> guid (Regel 8) — this type never touches our database at all.
/// </summary>
public sealed record ForeignEmoteRow(
    string SevenTvEmoteId,
    string Name,
    string DefaultName,
    string ImageUrl,
    int? TopAllTime,
    int? Trending);
