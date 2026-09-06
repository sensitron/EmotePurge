namespace EmotePurge.Worker;

/// <summary>
/// Decides whether a chat message came from a bot, once per message, out of the chatter id and the
/// IRC badges — nothing TwitchLib-specific, so it can be tested without a client (see
/// <see cref="BotChatterDetector"/>).
/// </summary>
public interface IBotChatterDetector
{
    /// <summary>
    /// Never throws — this runs on the hot path in <c>TwitchChatManager.OnMessageReceived</c>. A
    /// missing/empty <paramref name="chatterId"/> or a <c>null</c> <paramref name="badges"/> list
    /// just skips that half of the check rather than failing.
    /// </summary>
    bool IsBot(string? chatterId, IReadOnlyList<KeyValuePair<string, string>>? badges);

    /// <summary>
    /// Every account id this detector treats as a bot: the statically known ones plus whatever
    /// <c>Twitch:AdditionalBotAccountIds</c> adds. Read-only and exposed for one reason — the
    /// chat-log backfill harness (issue #69) puts the set into its report head, and reading it off
    /// the detector guarantees the report names exactly what <see cref="IsBot"/> decided on,
    /// instead of a second parse of the same configuration key that could drift from it.
    /// <para>
    /// Says nothing about the badge check: a message can be classified as a bot without its sender
    /// appearing here.
    /// </para>
    /// </summary>
    IReadOnlySet<string> KnownBotAccountIds { get; }
}
