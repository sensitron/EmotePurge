namespace EmotePurge.Worker.SevenTv;

public enum SevenTvSessionEndReason
{
    /// <summary>Server asked for a reconnect (op 4 / op 7) or ended the ~1h connection TTL.</summary>
    ServerRequestedReconnect,

    /// <summary>The socket closed from the remote side without a reconnect request.</summary>
    RemoteClosed,

    /// <summary>Three heartbeat intervals passed without any frame.</summary>
    HeartbeatTimeout,

    /// <summary>Close code 4009: the server considers a subscription duplicated.</summary>
    AlreadySubscribed,

    /// <summary>The connection attempt itself failed.</summary>
    ConnectFailed,

    /// <summary>Any unexpected exception in the session.</summary>
    Faulted
}

/// <summary>
/// <see cref="SessionEstablished"/> means a Hello was received — the session actually worked,
/// however it ended.
/// </summary>
public readonly record struct SevenTvSessionResult(bool SessionEstablished, SevenTvSessionEndReason Reason, int? CloseCode = null);

/// <summary>
/// Reconnect pacing for the 7TV EventAPI. Deliberately not <see cref="EmotePurge.Worker.ReconnectPolicy"/>:
/// that class decides reconnect-vs-recreate-vs-wait for a long-lived TwitchLib client object, with
/// thresholds calibrated on Twitch production outages. A ClientWebSocket is single-use — every
/// session builds a fresh one — so the only question here is how long to wait, and the answer must
/// treat the server's documented ~1h TTL disconnect as routine: an established session never
/// escalates the backoff. No attempt limit (the S2-1 lesson: a silently exhausted retry budget took
/// the Twitch connection down for good). Clock-free and with injectable jitter for deterministic tests.
/// </summary>
public sealed class SevenTvBackoffPolicy(Func<double>? jitter = null)
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ServerReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AlreadySubscribedFloor = TimeSpan.FromSeconds(60);

    // Cap the exponent, not just the delay, so the streak counter cannot grow unboundedly.
    private const int MaxFailureStreak = 6;

    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;
    private int _failureStreak;

    public TimeSpan NextDelay(SevenTvSessionResult result)
    {
        if (result.SessionEstablished)
        {
            _failureStreak = 0;

            if (result.Reason == SevenTvSessionEndReason.ServerRequestedReconnect)
            {
                // The server told us to come back; a fresh instance is waiting. ±20% jitter keeps
                // a fleet of connections from reconnecting in lockstep after a rolling restart.
                return Jittered(ServerReconnectDelay);
            }
        }
        else
        {
            _failureStreak = Math.Min(_failureStreak + 1, MaxFailureStreak);
        }

        var exponent = Math.Max(0, _failureStreak - 1);
        var raw = TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << exponent, MaxDelay.Ticks));
        var delay = Jittered(raw);

        if (result.Reason == SevenTvSessionEndReason.AlreadySubscribed && delay < AlreadySubscribedFloor)
        {
            // 4009 means the server disagrees with our view of the subscriptions; hammering it
            // with instant reconnects would turn a state mismatch into a connect loop.
            return AlreadySubscribedFloor;
        }

        return delay;
    }

    public void Reset() => _failureStreak = 0;

    private TimeSpan Jittered(TimeSpan raw) =>
        TimeSpan.FromTicks((long)(raw.Ticks * (0.8 + 0.4 * _jitter())));
}
