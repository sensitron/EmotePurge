namespace EmotePurge.Api.Health;

/// <summary>
/// The ceilings the admin roster reports against. Two separate numbers on the Twitch side, and they
/// are separate on purpose: one is Twitch's rule, the other is ours. Presenting either as the other
/// is how a support page starts lying — so each ships with its own field and the UI labels the
/// provenance rather than adding them up or picking the smaller one.
/// </summary>
internal static class WorkerCapacity
{
    /// <summary>
    /// Twitch's documented cap on how many chatrooms one account may be joined to at a time, in
    /// force since 2024-05-15 (dev.twitch.tv/docs/chat). Channels where the account is broadcaster
    /// or moderator are exempt, which ours never is. A verified bot account lifts it.
    /// </summary>
    public const int TwitchConcurrentChannelLimit = 100;

    /// <summary>
    /// Our own operating budget, well below the limit above, and the one that actually binds today.
    /// Reason: TwitchLib rejoins every channel itself after a reconnect and paces those JOINs not at
    /// all — roughly 180 ms apart, measured on prod — so above about twenty channels a single
    /// reconnect bursts straight through Twitch's 20-JOINs-per-10-seconds window and the excess
    /// channels lose their join until the next EnsureJoinedAsync sweep. Raising this needs sharding
    /// across several clients or a verified bot account, not a bigger number here.
    /// </summary>
    public const int TwitchJoinBudgetChannels = 20;
}
