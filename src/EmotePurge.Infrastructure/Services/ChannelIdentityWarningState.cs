using System.Collections.Concurrent;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Remembers which channels have already been reported as "Twitch does not know this one", so the
/// reconcile loop warns on the state change instead of once per tick forever. Neither half of that
/// bar is optional: a channel whose account was deleted would otherwise produce a warning every
/// hour for as long as the row exists, and dropping the warning entirely would hide the one signal
/// that says a tracked channel is gone.
/// <para>
/// A singleton rather than a field on <see cref="ChannelIdentityService"/>, which is scoped like
/// every other service that takes the <c>AppDbContext</c>: the worker opens a fresh scope per tick,
/// so a per-service set would be empty on every pass and would deduplicate nothing. Deliberately
/// not a <c>static</c> field either — that leaks between tests in the same process. Same shape and
/// same reason as <see cref="ChannelSyncGate"/> and <see cref="DuplicateEmoteNameTracker"/>.
/// </para>
/// <para>
/// Process-local on purpose: after a restart every still-broken channel is reported once more,
/// which is the desirable half of the trade — the alternative (persisting it) would mean a channel
/// that broke while the process was down could go unreported forever.
/// </para>
/// </summary>
public sealed class ChannelIdentityWarningState
{
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// <summary>
    /// True the first time <paramref name="key"/> is reported and false while it stays reported —
    /// so the caller can write <c>if (ShouldWarn(key)) LogWarning(...)</c>.
    /// </summary>
    public bool ShouldWarn(string key) => _reported.TryAdd(key, 0);

    /// <summary>
    /// Forgets <paramref name="key"/>, so a channel that resolves and later breaks again is
    /// reported again rather than silently.
    /// </summary>
    public void Clear(string key) => _reported.TryRemove(key, out _);

    // Two key spaces in one dictionary, kept apart by prefix: a login and a Twitch id are different
    // facts about a channel and must not silence each other.
    public static string LoginKey(string normalizedChannelName) => $"login:{normalizedChannelName}";

    public static string IdKey(string twitchChannelId) => $"id:{twitchChannelId}";

    // Third key space: a rename that cannot proceed because another row holds the target name.
    // Usually resolved by the next tick, but if the blocking row is itself unreconcilable the pair
    // never converges — and then this is the same hourly flood as a dead login.
    public static string BlockedKey(string channelId) => $"blocked:{channelId}";

    // Fourth key space: a merge refused because the row to be dissolved still has emotes. Keyed on
    // the loser row, which is the thing a human has to deal with, and which both ends of the
    // duplicate pair agree on. Unlike the blocked case this one is *never* self-resolving — it waits
    // for a person — so undeduplicated it would warn hourly for as long as the process lives.
    public static string RefusedKey(string loserChannelId) => $"refused:{loserChannelId}";
}
