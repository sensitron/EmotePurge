namespace EmotePurge.Core.Chat;

/// <summary>
/// The three-way answer to "whose chat room is this message from", as decided by
/// <see cref="SharedChatRule"/>. See that class for the rule itself.
/// </summary>
public enum MessageOrigin
{
    /// <summary>The normal case: no Shared Chat session, or a session's origin channel.</summary>
    Own,

    /// <summary>A Shared Chat message mirrored in from a different room.</summary>
    Foreign,

    /// <summary>
    /// The message carries Shared Chat markers but not a usable <c>source-room-id</c> — it says
    /// "I am part of a session" without saying whose. Counted apart from <see cref="Own"/>, not
    /// folded into it.
    /// </summary>
    Indeterminate
}
