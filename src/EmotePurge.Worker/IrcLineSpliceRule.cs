namespace EmotePurge.Worker;

/// <summary>
/// Detects an IRC line that can only exist because two lines were spliced together on the same
/// socket (issue #114): TwitchLib's inline <c>RECONNECT</c> handling leaves the old read loop
/// running alongside the new one for a brief window, and both can hand fragments of different
/// lines to the same parser.
/// <para>
/// This reads <c>ChatMessage.RawIrcMessage</c>, not <c>ChatMessage.UndocumentedTags</c> (E6). A splice
/// that lands inside the *value* of a typed tag — e.g. <c>subscriber=0@badge-info=</c>, where the
/// second line's tag block starts mid-value — never surfaces as a second dictionary key: TwitchLib's
/// tag parser splits the tag block on <c>;</c>, so the extra <c>@…</c> stays swallowed inside the
/// value of the tag it landed in. Only the raw, unparsed line still shows the second <c>@</c>.
/// </para>
/// <para>
/// The rule cannot fire on the two other places a line legitimately carries a second <c>@</c>: the
/// IRC prefix (<c>nick!user@host</c>) and an <c>@user</c> mention in the message text both sit
/// after the first space, i.e. outside the tag block this rule inspects.
/// </para>
/// <para>
/// Known lower bound: a splice severe enough to make the line unparsable never reaches
/// <see cref="TwitchChatManager.OnMessageReceived"/> at all (TwitchLib drops it before raising the
/// event), so this rule — and the counter it feeds — undercounts the true splice rate. It proves
/// the defect exists; it does not measure it exhaustively.
/// </para>
/// </summary>
public static class IrcLineSpliceRule
{
    /// <summary>
    /// Default cap for <see cref="TagBlockForLog"/>: long enough to show a full, legitimate tag
    /// block, short enough to bound a single log line even for a pathologically long splice.
    /// </summary>
    public const int DefaultMaxTagBlockLengthForLog = 512;

    /// <summary>
    /// True if <paramref name="rawLine"/> is an IRC line whose tag block (the segment up to the
    /// first space, or the whole line if there is no space) contains a second <c>@</c> after the
    /// leading one. Allocation-free: no <c>Split</c>/<c>Substring</c>, just <see cref="string.IndexOf(char)"/>.
    /// </summary>
    public static bool IsSpliced(string? rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
        {
            return false;
        }

        if (rawLine[0] != '@')
        {
            return false;
        }

        var tagBlockLength = rawLine.IndexOf(' ');
        if (tagBlockLength < 0)
        {
            tagBlockLength = rawLine.Length;
        }

        return rawLine.IndexOf('@', 1, tagBlockLength - 1) >= 0;
    }

    /// <summary>
    /// Extracts the tag block of <paramref name="rawLine"/> for logging: the segment up to the
    /// first space, or the whole line if there is no space, capped at <paramref name="maxLength"/>
    /// characters. Used by the splice sentinel (#114) to log enough of the line to diagnose the
    /// defect without ever including the message text (data minimisation) or letting a
    /// pathologically long tag block blow up a log line. <c>null</c> or empty input yields an
    /// empty string, so callers never need to guard the result.
    /// </summary>
    public static string TagBlockForLog(string? rawLine, int maxLength = DefaultMaxTagBlockLengthForLog)
    {
        if (string.IsNullOrEmpty(rawLine))
        {
            return string.Empty;
        }

        var tagBlockEnd = rawLine.IndexOf(' ');
        var tagBlock = tagBlockEnd < 0 ? rawLine : rawLine[..tagBlockEnd];
        return tagBlock.Length > maxLength ? tagBlock[..maxLength] : tagBlock;
    }
}
