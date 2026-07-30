namespace EmotePurge.Core.Entities;

/// <summary>
/// Owns the one invariant every channel lookup in this codebase silently depends on:
/// <see cref="Channel.ChannelName"/> is stored trimmed and lower-case, so every read filters on the
/// normalized form. That invariant used to live only in 28 hand-written
/// <c>Trim().ToLowerInvariant()</c> expressions — nothing named it, nothing enforced it, and a future
/// write path that stored a mixed-case name would have made every one of those lookups quietly find
/// nothing (Twitch logins are commonly typed with capitals, e.g. "HandOfBlood").
/// <para>
/// Anything that writes <see cref="Channel.ChannelName"/> or filters on it goes through
/// <see cref="Normalize"/>. Note this is the *storage* normalization only — the format check lives
/// in the API's ChannelNameValidation, which normalizes through here before matching.
/// </para>
/// </summary>
public static class ChannelName
{
    public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
