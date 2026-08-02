namespace EmotePurge.Core.Services;

/// <summary>
/// Outcome of trying to claim a channel's manual-resync slot. When <paramref name="Acquired"/> is
/// false, <paramref name="RetryAfterSeconds"/> says how long the caller has to wait — always at
/// least 1, so a client never renders "try again in 0 seconds".
/// </summary>
public readonly record struct ResyncCooldownState(bool Acquired, int RetryAfterSeconds);

/// <summary>
/// Rate-limits manual resyncs <b>per channel</b>, which is the dimension the ASP.NET rate limiter
/// cannot express: its partitions are per user, so fifteen moderators of one channel each get their
/// own budget and the channel gets fifteen times as many.
/// <para>
/// What a resync actually costs: one unconditional 7TV REST call, a handful of database round-trips,
/// and — the expensive part — one <c>channel.synced</c> live event that makes every open page for
/// that channel refetch, including the heaviest aggregate query in the app. The people paying for a
/// resync-spam are the viewers with a page open, not the person clicking.
/// </para>
/// <para>
/// Deliberately a service of its own rather than a step inside <c>IChannelService</c>: only the
/// endpoint may consult it. The worker's periodic resync must never see it, and keeping it out of
/// <c>ChannelService</c> also keeps that class's constructor free of a Redis dependency.
/// </para>
/// </summary>
public interface IChannelResyncCooldown
{
    /// <summary>
    /// Claims the slot if it is free. Atomic — two simultaneous callers cannot both acquire it, so
    /// there is no window between checking and triggering.
    /// </summary>
    Task<ResyncCooldownState> TryBeginAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the slot back when the resync did not actually happen (unknown or inactive channel).
    /// Without this, asking about a channel that is not tracked would block the real resync for a
    /// full cooldown — exactly the moment a broadcaster who just rejoined wants one.
    /// </summary>
    Task ReleaseAsync(string channelName, CancellationToken cancellationToken = default);
}
