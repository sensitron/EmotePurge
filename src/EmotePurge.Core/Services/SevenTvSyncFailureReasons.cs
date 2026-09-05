using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// Why the last full 7TV sync attempt for a channel produced nothing, as a stable, language-neutral
/// code (Regel 7). A string contract rather than <see cref="SevenTvLookupStatus"/> itself, for the
/// same two reasons as <c>ChannelLiveStates</c> and <c>ApiErrorCodes</c>: the JSON wire value is the
/// value named here, independent of serializer enum settings — and the enum carries an
/// <see cref="SevenTvLookupStatus.Ok"/> member that must never appear on the wire.
/// <para>
/// Mirrored in <c>web/src/app/core/emotes/seven-tv-sync-failure.ts</c>; every code needs a
/// <c>sevenTvSync.failure.<code></c> block in <b>both</b> locale files.
/// </para>
/// </summary>
public static class SevenTvSyncFailureReasons
{
    /// <summary>No 7TV account carries this Twitch channel's connection at all.</summary>
    public const string NoSevenTvAccount = "no_seventv_account";

    /// <summary>
    /// The 7TV account exists, but no emote set is active on it — the case behind issue #32, and
    /// the only one the channel owner can fix themselves in a minute.
    /// </summary>
    public const string NoActiveEmoteSet = "no_active_emote_set";

    /// <summary>7TV could not be reached or answered with an error. Transient by nature.</summary>
    public const string Unavailable = "seventv_unavailable";

    /// <summary>
    /// 7TV answered, and the answer parsed, but it does not carry what the sync needs — today only
    /// one shape: an active set without an id. Deliberately distinct from <see cref="Unavailable"/>,
    /// which means "we never got an answer": here we did, and it was wrong.
    /// <para>
    /// The one reason with no <see cref="SevenTvLookupStatus"/> behind it, and that is the point.
    /// The statuses describe what a *lookup* concluded; this describes a payload that passed every
    /// one of those checks and still cannot be used. Giving the enum a member for it would mean
    /// every lookup in the codebase suddenly has to consider a case that only the sync can detect.
    /// So <see cref="FromStatus"/> stays exhaustive over the enum and never returns this value —
    /// <c>SevenTvSyncService</c> uses it directly, at the one place that can see the defect.
    /// </para>
    /// </summary>
    public const string ResponseUnusable = "seventv_response_unusable";

    /// <summary>
    /// The single mapping point between the internal control flow and the wire contract.
    /// Returns <c>null</c> for <see cref="SevenTvLookupStatus.Ok"/>: a success has no reason, and
    /// that absence is what the persisted column uses to mean "the last attempt worked".
    /// </summary>
    public static string? FromStatus(SevenTvLookupStatus status) => status switch
    {
        SevenTvLookupStatus.Ok => null,
        SevenTvLookupStatus.NoSevenTvAccount => NoSevenTvAccount,
        SevenTvLookupStatus.NoActiveEmoteSet => NoActiveEmoteSet,
        SevenTvLookupStatus.Unavailable => Unavailable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unbekannter 7TV-Lookup-Status — kein Fehlergrund zuzuordnen.")
    };
}
