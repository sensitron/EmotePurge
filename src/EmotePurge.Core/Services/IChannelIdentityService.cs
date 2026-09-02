using EmotePurge.Core.Twitch;

namespace EmotePurge.Core.Services;

/// <summary>
/// What Twitch had to say about one login or id. Three states rather than a nullable identity,
/// because "Twitch has no such account" and "we could not ask Twitch" must never collapse into one
/// answer: the first is a fact a caller may act on, the second is the absence of a fact. Helix
/// signals the difference itself — an empty <c>data</c> array in a successful response versus a
/// failed request (see <see cref="ITwitchHelixClient.GetUsersAsync"/>).
/// </summary>
public enum TwitchUserLookupStatus
{
    Found,
    NotFound,
    Unavailable
}

/// <summary><see cref="User"/> is non-null exactly for <see cref="TwitchUserLookupStatus.Found"/>.</summary>
public record TwitchUserLookup(TwitchUserLookupStatus Status, TwitchUserIdentity? User);

/// <summary>
/// What one reconcile pass did, for the worker's log line. Every counter is a write except
/// <see cref="Checked"/> (rows examined) and <see cref="LoginsMissing"/> (rows Twitch no longer
/// knows, under either their login or their id — deliberately counted but never acted on).
/// </summary>
public record ChannelIdentityReconcileSummary(
    int Checked,
    int IdsBackfilled,
    int Renamed,
    int Merged,
    int MergesRefused,
    int LoginsMissing);

/// <summary>
/// Keeps the stored channel rows in step with Twitch's own view of who they are. The immutable
/// Twitch id is the channel's identity; the login is a display name that its owner may change at
/// any time, and Twitch does not tell anyone when they do — so the only way to notice is to ask.
/// </summary>
public interface IChannelIdentityService
{
    /// <summary>
    /// Asks Helix about every active channel in one request and brings the rows in line: backfills
    /// missing ids, follows renames, and merges the duplicate row a rename can leave behind.
    /// <para>
    /// <c>null</c> means the tick was skipped without writing anything — no app token, or Helix
    /// unreachable. That is deliberately not an empty summary: "nothing needed doing" and "we never
    /// found out" are different states, and only the second one is worth retrying immediately.
    /// </para>
    /// </summary>
    Task<ChannelIdentityReconcileSummary?> ReconcileActiveChannelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves one login to its Twitch identity — the app-token handling and the Helix call the
    /// join path needs, in one place. Callers must treat
    /// <see cref="TwitchUserLookupStatus.Unavailable"/> as "carry on as before" rather than as a
    /// rejection: an outage on our side is not evidence about the channel.
    /// </summary>
    Task<TwitchUserLookup> LookupByLoginAsync(string login, CancellationToken ct = default);
}
