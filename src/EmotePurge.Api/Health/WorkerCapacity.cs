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
    /// The reason changed on 2026-09-08 (#68, #114): the worker now drives its own reconnect, and
    /// every rejoin — including after a reconnect — runs through the same 600ms-throttled join path
    /// as everything else, so there is no longer an unthrottled TwitchLib rejoin to burst through
    /// Twitch's 20-JOINs-per-10-seconds window. What still bounds this number: a rejoin after any
    /// reconnect takes linear wall-clock time per channel (at 100 channels, roughly 60s of gap
    /// before the last one is confirmed), Twitch's 100-chatroom cap above, and the 7TV EventAPI's
    /// own subscription ceiling (~250 channels at two subscriptions each).
    ///
    /// A probe run from this worker's anonymous connection (comment on #68, 2026-09-08) measured
    /// both of the limits this constant used to invoke and found neither one bit for that anonymous
    /// connection: 208 JOINs across five staged runs, all confirmed, peaked at 45 in a single
    /// 10-second window — well past the 20/10s figure the JOIN rate limit describes, with no
    /// rejection. Twitch's 100-chatroom cap didn't bite either, up to 120 simultaneously joined
    /// channels; a named-account run under the same setup hit it at channel 101 with an explicit
    /// msg_concurrent_channel_limit_reached, so the cap is bound to the account, not to the client ID
    /// an anonymous connection presents. Read this as data, not as a green light to raise the
    /// constant: the probe measured neither sustained operation over hours, real chat volume, nor
    /// worker memory under load, and 120 is the highest value it actually exercised, not a verified
    /// ceiling. If this budget ever moves, it needs its own measurement against those three — sharding
    /// across several clients or a verified bot account — not just a bigger number here.
    /// </summary>
    public const int TwitchJoinBudgetChannels = 20;
}
