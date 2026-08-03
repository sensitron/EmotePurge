using EmotePurge.Core.Services;

namespace EmotePurge.Api.Health;

/// <summary>
/// Everything derived from a <see cref="WorkerHealthSnapshot"/>: the two status strings and the
/// staleness numbers behind them. Extracted when the admin health endpoint arrived (Z1 split) so
/// the public and the admin view cannot drift into reporting different states from one snapshot —
/// the thresholds exist exactly once.
/// </summary>
internal static class WorkerHealthStatus
{
    // Mirrors TwitchWatchdogPolicy.FrameStaleThreshold (3 × Twitch's ~5-minute server-PING cadence,
    // since 2026-08-03 measured on received frames, not chat messages). A literal because the Api
    // and the Worker share no code here beyond the snapshot contract.
    public const int StaleAfterSeconds = 900;

    // Mirrors the 7TV event client's heartbeat watchdog (3 × ~45s heartbeat interval, plus slack).
    // Same literal-by-design reasoning as StaleAfterSeconds above.
    public const int SevenTvStaleAfterSeconds = 150;

    // 7TV's documented per-connection subscription_limit. Shipped to the admin UI so the utilization
    // bar has a denominator without hard-coding it a second time in TypeScript.
    public const int SevenTvSubscriptionLimit = 500;

    public static WorkerHealthDerived Derive(WorkerHealthSnapshot snapshot, DateTime nowUtc)
    {
        var secondsSinceLastMessage = snapshot.LastMessageReceivedUtc is { } lastMessage
            ? (int)(nowUtc - lastMessage).TotalSeconds
            : (int?)null;

        // Deriving the status from isConnected alone let the endpoint report "connected" while
        // nothing was arriving — the flag can lag reality (silent freeze) and, before the
        // recreate path reset it, could stay true on a client that had already been discarded.
        // "stale" therefore also covers "connected, but nothing received for a while". Keys off
        // received frames (Twitch's server PING keeps a healthy-but-silent connection fresh, same
        // reasoning as the 7TV derivation below); the chat-message fallback covers a payload from
        // a worker predating the field during a rolling deploy, the connect attempt covers a
        // worker that just started and has received nothing yet.
        var quietSince = snapshot.TwitchLastFrameUtc
            ?? snapshot.LastMessageReceivedUtc
            ?? snapshot.ConnectAttemptedUtc;
        var quietForSeconds = quietSince is { } since ? (int)(nowUtc - since).TotalSeconds : (int?)null;
        var status = snapshot.IsConnected switch
        {
            false => "disconnected",
            true when quietForSeconds is null or > StaleAfterSeconds => "stale",
            true => "connected",
        };

        // 7TV staleness keys off frames (heartbeats arrive every ~45s), never off dispatches —
        // a channel whose emote set nobody edits gets no dispatches for days and would
        // otherwise read as permanently stale. "disabled" keeps a deliberately switched-off
        // event path distinguishable from a broken one.
        var sevenTvQuietSince = snapshot.SevenTvLastFrameUtc ?? snapshot.SevenTvConnectAttemptedUtc;
        var sevenTvQuietForSeconds = sevenTvQuietSince is { } sevenTvSince
            ? (int)(nowUtc - sevenTvSince).TotalSeconds
            : (int?)null;
        var sevenTvStatus = snapshot switch
        {
            { SevenTvEnabled: false } => "disabled",
            { SevenTvConnected: false } => "disconnected",
            _ when sevenTvQuietForSeconds is null or > SevenTvStaleAfterSeconds => "stale",
            _ => "connected",
        };

        return new WorkerHealthDerived(status, secondsSinceLastMessage, sevenTvStatus, sevenTvQuietForSeconds);
    }
}

internal readonly record struct WorkerHealthDerived(
    string Status,
    int? SecondsSinceLastMessage,
    string SevenTvStatus,
    int? SevenTvSecondsSinceLastFrame);
