using System.Globalization;
using System.Text;
using EmotePurge.Core.ChatLogArchive;

namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>
/// Parses one line of the justlog <c>?raw</c> export format: an IRCv3-tagged Twitch IRC line,
/// <c>@tags :prefix COMMAND #channel :trailing</c>. Every real line the archive exports carries
/// both the <c>@tags</c> and <c>:prefix</c> segments (measured live, T8) — a line missing either
/// is not a recognizable IRC line at all and counts as malformed, not as some unrecognized
/// command.
/// <para>
/// <b>Known edge, deliberately out of scope.</b> Plain IRC itself allows tag-less lines that would
/// be perfectly valid commands elsewhere — a bare <c>PING</c>, or a <c>JOIN</c>/<c>PART</c> without
/// tags. This parser counts such a line as malformed, not as an unrecognized command, because it
/// never carries the <c>@tags</c>/<c>:prefix</c> shape. That is intentional, not an oversight: the
/// justlog per-day chat export never emits such lines (T8), and treating "no tags, no prefix" as
/// "could be any command" would make the <see cref="ChatLogDayStatus.MalformedResponse"/> ratio
/// unreachable — a body of pure garbage text (no <c>@</c>/<c>:</c> in sight) would otherwise count
/// as a pile of unrecognized-but-valid commands instead of tripping the malformed-ratio check.
/// </para>
/// <para>
/// Only <c>PRIVMSG</c> lines produce a <see cref="ChatLogMessage"/>. Every other recognized
/// command (<c>CLEARCHAT</c>, <c>USERNOTICE</c>, <c>CLEARMSG</c>, …) returns <c>false</c> with
/// <c>ircCommand</c> set to that command, so <see cref="ChatLogArchiveClient"/> can count it apart
/// from a line that could not be read at all.
/// </para>
/// <para>
/// A missing/empty <c>user-id</c> or <c>badges</c> tag is not treated as an error: both are
/// nullable/possibly-empty on <see cref="ChatLogMessage"/>, and deciding what an absence means is
/// the harness's job (design doc, "Rückfall ohne Badges"), not this parser's.
/// </para>
/// </summary>
public static class JustlogRawLineParser
{
    private const string ActionPrefix = "\u0001ACTION ";
    private const char ActionSuffix = '\u0001';

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoBadges = [];

    /// <summary>
    /// <c>false</c> for anything that is not a complete, parsable PRIVMSG line.
    /// <paramref name="ircCommand"/> carries the recognized command (e.g. <c>"CLEARCHAT"</c>) when
    /// the line was readable as *some* IRC command other than PRIVMSG; it stays <c>null</c> when
    /// the line could not be read as an IRC line at all (empty line, missing tags/prefix, a
    /// PRIVMSG line missing its trailing text, or an unparsable timestamp).
    /// </summary>
    public static bool TryParse(string line, out ChatLogMessage message, out string? ircCommand)
    {
        message = null!;
        ircCommand = null;

        if (string.IsNullOrEmpty(line) || line[0] != '@')
        {
            return false;
        }

        var tagsEnd = line.IndexOf(' ', 1);
        if (tagsEnd < 0)
        {
            return false;
        }

        var tags = ParseTags(line[1..tagsEnd]);
        var afterTags = line[(tagsEnd + 1)..];

        if (afterTags.Length == 0 || afterTags[0] != ':')
        {
            return false;
        }

        var prefixEnd = afterTags.IndexOf(' ');
        if (prefixEnd < 0)
        {
            return false;
        }

        var afterPrefix = afterTags[(prefixEnd + 1)..];
        var commandEnd = afterPrefix.IndexOf(' ');
        var command = commandEnd < 0 ? afterPrefix : afterPrefix[..commandEnd];
        var paramsAndTrailing = commandEnd < 0 ? null : afterPrefix[(commandEnd + 1)..];

        if (command.Length == 0)
        {
            return false;
        }

        if (!string.Equals(command, "PRIVMSG", StringComparison.Ordinal))
        {
            ircCommand = command;
            return false;
        }

        if (!TryExtractTrailing(paramsAndTrailing, out var trailingText))
        {
            return false;
        }

        if (!tags.TryGetValue("tmi-sent-ts", out var sentAtRaw) ||
            !long.TryParse(sentAtRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentAtEpochMs))
        {
            return false;
        }

        tags.TryGetValue("user-id", out var userId);
        tags.TryGetValue("room-id", out var roomId);
        tags.TryGetValue("source-room-id", out var sourceRoomId);
        tags.TryGetValue("badges", out var badgesRaw);

        message = new ChatLogMessage(
            DateTimeOffset.FromUnixTimeMilliseconds(sentAtEpochMs).UtcDateTime,
            string.IsNullOrEmpty(userId) ? null : userId,
            ParseBadges(badgesRaw),
            string.IsNullOrEmpty(roomId) ? null : roomId,
            string.IsNullOrEmpty(sourceRoomId) ? null : sourceRoomId,
            UnpackAction(trailingText));

        return true;
    }

    // The trailing part is everything after the first " :" once past the command — including any
    // further colons or spaces the message text itself contains (see the doubled-colon test case).
    // A bare leading ':' with no preceding params (e.g. "PRIVMSG :hi") is the same trailing form
    // with the command's own space already consumed above.
    private static bool TryExtractTrailing(string? paramsAndTrailing, out string trailingText)
    {
        trailingText = string.Empty;
        if (paramsAndTrailing is null)
        {
            return false;
        }

        var separator = paramsAndTrailing.IndexOf(" :", StringComparison.Ordinal);
        if (separator >= 0)
        {
            trailingText = paramsAndTrailing[(separator + 2)..];
            return true;
        }

        if (paramsAndTrailing.Length > 0 && paramsAndTrailing[0] == ':')
        {
            trailingText = paramsAndTrailing[1..];
            return true;
        }

        return false;
    }

    // CTCP ACTION framing (the "/me" form), unpacked the same way TwitchLib's ChatMessage presents
    // it to the live path: strip the "ACTION " prefix and trailing "", nothing else.
    private static string UnpackAction(string trailingText)
    {
        if (trailingText.Length > ActionPrefix.Length &&
            trailingText.StartsWith(ActionPrefix, StringComparison.Ordinal) &&
            trailingText[^1] == ActionSuffix)
        {
            return trailingText[ActionPrefix.Length..^1];
        }

        return trailingText;
    }

    // Splitting on the raw ';' is safe even though tag values are escaped: IRCv3 escapes a literal
    // ';' inside a value as the two-character sequence "\:", so an actual unescaped ';' can only
    // ever be a tag separator.
    private static Dictionary<string, string> ParseTags(string tagsSpan)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tagsSpan.Length == 0)
        {
            return result;
        }

        foreach (var entry in tagsSpan.Split(';'))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            var eqIndex = entry.IndexOf('=');
            if (eqIndex < 0)
            {
                result[entry] = string.Empty;
            }
            else
            {
                result[entry[..eqIndex]] = UnescapeTagValue(entry[(eqIndex + 1)..]);
            }
        }

        return result;
    }

    // IRCv3 tag-value escaping table (https://ircv3.net/specs/extensions/message-tags.html):
    // \: -> ; , \s -> space, \\ -> \, \r -> CR, \n -> LF. Any other escaped character just drops
    // the backslash, per spec.
    private static string UnescapeTagValue(string raw)
    {
        if (raw.IndexOf('\\') < 0)
        {
            return raw;
        }

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var current = raw[i];
            if (current == '\\' && i + 1 < raw.Length)
            {
                i++;
                builder.Append(raw[i] switch
                {
                    ':' => ';',
                    's' => ' ',
                    '\\' => '\\',
                    'r' => '\r',
                    'n' => '\n',
                    var other => other
                });
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    // "Set-Id, Version" pairs — same shape IBotChatterDetector.IsBot already expects from the live
    // path's TwitchLib badges. An empty/missing tag yields an empty list, not an error.
    private static IReadOnlyList<KeyValuePair<string, string>> ParseBadges(string? badgesRaw)
    {
        if (string.IsNullOrEmpty(badgesRaw))
        {
            return NoBadges;
        }

        var result = new List<KeyValuePair<string, string>>();
        foreach (var entry in badgesRaw.Split(','))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            var slashIndex = entry.IndexOf('/');
            result.Add(slashIndex < 0
                ? new KeyValuePair<string, string>(entry, string.Empty)
                : new KeyValuePair<string, string>(entry[..slashIndex], entry[(slashIndex + 1)..]));
        }

        return result;
    }
}
