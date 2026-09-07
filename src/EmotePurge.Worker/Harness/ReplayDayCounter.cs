using EmotePurge.Core.Chat;
using EmotePurge.Core.Matching;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// Counts one archive day of the chat-log backfill harness (#69): one instance per day, fed message
/// by message, closed once with <see cref="Finish"/>. Pure — no I/O, no logger, no clock beyond the
/// timestamps handed in, no state that survives the day.
/// <para>
/// The matching rule itself is <b>not</b> reimplemented here: every hit goes through
/// <see cref="EmoteNameMatching"/>, the same class the live chat path uses, because the whole point
/// of the harness is to count the way the live measurement counts. What this class adds on top is
/// the day-boundary decision (which emotes may be hit on this day at all) and the diagnostics.
/// </para>
/// <para>
/// No chatter id leaves this class. The k-distribution keeps a set of distinct chatters per
/// (emote, day) cell while the day is running and collapses it into
/// <see cref="ReplayDayLine.KHistogram"/> in <see cref="Finish"/>; the sets are dropped there.
/// </para>
/// </summary>
public sealed class ReplayDayCounter
{
    /// <summary>Cells with more chatters than this are reported as "10+" in the last bucket.</summary>
    private const int HistogramCap = 10;

    private const int HistogramLength = HistogramCap + 1;

    /// <summary>
    /// Stands in for every message without a chatter id, so such messages collapse into a single
    /// pseudo chatter instead of inflating the k-distribution. It cannot collide with a real Twitch
    /// user id, which is numeric.
    /// </summary>
    private const string UnknownChatter = "unknown";

    private static readonly string[] ReasonNames = Enum.GetNames<UnmatchedReason>();

    private readonly DateOnly _day;
    private readonly Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> _isBot;
    private readonly Dictionary<string, ReplayEmote> _emotesById;
    private readonly Dictionary<string, string> _dayMap;
    private readonly IReadOnlySet<string> _dayAmbiguousNames;
    private readonly Dictionary<string, string> _channelMap;
    private readonly Dictionary<string, int> _humanCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _botCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sharedChatCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unmatchedByReason = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _cells = new(StringComparer.Ordinal);
    private readonly HashSet<string> _humanChatters = new(StringComparer.Ordinal);

    private int _messageCount;
    private int _botMessageCount;
    private int _sharedChatMessageCount;
    private int _indeterminateMessageCount;
    private int _outsideDayCount;
    private int _firstSeenUnknownHits;
    private bool _finished;

    /// <param name="day">The archive day this instance counts; the log file for it is the truth.</param>
    /// <param name="emotes">
    /// Every emote of the channel, archived ones included. Two maps are coalesced out of it: the
    /// day map (emotes whose lifetime covers <paramref name="day"/>) that hits are counted against,
    /// and a channel-wide second map that only serves to name the reason for a miss.
    /// </param>
    /// <param name="isBot">
    /// The live bot classifier (<see cref="IBotChatterDetector"/>), passed in as a function so this
    /// class stays free of the worker's service graph.
    /// </param>
    public ReplayDayCounter(
        DateOnly day,
        IReadOnlyList<ReplayEmote> emotes,
        Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> isBot)
    {
        ArgumentNullException.ThrowIfNull(emotes);
        ArgumentNullException.ThrowIfNull(isBot);

        _day = day;
        _isBot = isBot;

        _emotesById = new Dictionary<string, ReplayEmote>(StringComparer.Ordinal);
        foreach (var emote in emotes)
        {
            _emotesById.TryAdd(emote.Id, emote);
        }

        // Coalescing in the caller's order, exactly as the live match cache does. The order here is
        // by emote id (that is what the query hands over) and deliberately not the live load order,
        // which nobody knows; the ambiguous names are therefore reported rather than resolved.
        var dayMap = EmoteNameMatching.Coalesce(
            emotes.Where(e => CoversDay(e, day)).Select(e => new KeyValuePair<string, string>(e.Name, e.Id)));
        _dayMap = dayMap.NameToId;
        _dayAmbiguousNames = dayMap.AmbiguousNames;
        _channelMap = EmoteNameMatching.Coalesce(
            emotes.Select(e => new KeyValuePair<string, string>(e.Name, e.Id))).NameToId;
    }

    /// <summary>
    /// Counts one chat message of the archive day and returns the category it fell into. The seven
    /// values are the fields of a parsed log line; the message record of the log client is
    /// deliberately not referenced here, so that this half of the harness does not depend on the
    /// fetching half.
    /// <para>
    /// A message whose timestamp falls on another day still counts — the day file is the archive's
    /// truth and <see cref="ReplayDayLine.OutsideDayCount"/> is diagnostics. A message from a
    /// foreign or indeterminate room (<see cref="SharedChatRule"/>) is not "still counted the same
    /// way" any more: its hits move <see cref="ReplayDayLine.SharedChatCounts"/> instead of
    /// <see cref="ReplayDayLine.HumanCounts"/> or <see cref="ReplayDayLine.BotCounts"/> — the way the
    /// live worker has counted since #73 — and it is not tokenized any further: it moves nothing but
    /// its own message counter and that one shared-chat hit, never
    /// <see cref="ReplayDayLine.FirstSeenUnknownHits"/>, a k-distribution cell, or the human-chatter
    /// set, because those exist to explain this channel's own day map and its own chatters, not a
    /// foreign channel's coincidental name overlap with it.
    /// </para>
    /// </summary>
    public UsageCategory Count(
        DateTime sentAtUtc,
        string? userId,
        IReadOnlyList<KeyValuePair<string, string>> badges,
        string? roomId,
        string? sourceRoomId,
        bool hasOtherSourceMarkers,
        string text)
    {
        ThrowIfFinished();

        _messageCount++;

        if (DateOnly.FromDateTime(sentAtUtc) != _day)
        {
            _outsideDayCount++;
        }

        var origin = SharedChatRule.Classify(roomId, sourceRoomId, hasOtherSourceMarkers);
        if (origin == MessageOrigin.Foreign)
        {
            _sharedChatMessageCount++;
        }
        else if (origin == MessageOrigin.Indeterminate)
        {
            _indeterminateMessageCount++;
        }

        // _isBot is a call into the live bot classifier per message. UsageCategoryRule.Resolve
        // ignores isBot for anything but Own, so the call is skipped for foreign/indeterminate
        // messages — but Resolve is still called with false, so the room-vs-bot precedence stays
        // decided at this one spot (Plan decision 5) instead of a second "is this even own" check
        // creeping in here.
        var isBot = origin == MessageOrigin.Own && _isBot(userId, badges);
        var category = UsageCategoryRule.Resolve(origin, isBot);
        var chatter = string.IsNullOrEmpty(userId) ? UnknownChatter : userId;

        if (category == UsageCategory.Human)
        {
            _humanChatters.Add(chatter);
        }
        else if (category == UsageCategory.Bot)
        {
            _botMessageCount++;
        }

        if (category == UsageCategory.SharedChat)
        {
            foreach (var emoteId in EmoteNameMatching.MatchEmoteIds(text, _dayMap))
            {
                _sharedChatCounts[emoteId] = _sharedChatCounts.GetValueOrDefault(emoteId) + 1;
            }

            return category;
        }

        var counts = category == UsageCategory.Bot ? _botCounts : _humanCounts;
        foreach (var emoteId in EmoteNameMatching.MatchEmoteIds(text, _dayMap))
        {
            counts[emoteId] = counts.GetValueOrDefault(emoteId) + 1;

            if (_emotesById.TryGetValue(emoteId, out var emote) && emote.FirstSeenAt is null)
            {
                _firstSeenUnknownHits++;
            }

            if (category == UsageCategory.Human)
            {
                if (!_cells.TryGetValue(emoteId, out var chatters))
                {
                    _cells[emoteId] = chatters = new HashSet<string>(StringComparer.Ordinal);
                }

                chatters.Add(chatter);
            }
        }

        ClassifyTokens(text);

        return category;
    }

    /// <summary>
    /// Closes the day and returns its protocol line. The caller supplies what it alone knows: the
    /// fetch status, the transferred bytes, the hash of the body and the two line counters of the
    /// parser. Calling this twice is a bug (the chatter sets are gone afterwards) and throws.
    /// </summary>
    public ReplayDayLine Finish(string status, long bytes, string? bodySha256Hex, int nonPrivmsg, int malformed)
    {
        ThrowIfFinished();
        _finished = true;

        var histogram = new int[HistogramLength];
        foreach (var chatters in _cells.Values)
        {
            histogram[Math.Min(chatters.Count, HistogramCap)]++;
        }

        var cellCount = _cells.Count;
        var distinctChatters = _humanChatters.Count;

        // The point of no return for the privacy promise: from here on the counter holds no chatter
        // id any more, and the line below carries none either.
        _cells.Clear();
        _humanChatters.Clear();

        return new ReplayDayLine(
            _day,
            status,
            bytes,
            bodySha256Hex,
            _messageCount,
            _botMessageCount,
            _sharedChatMessageCount,
            _indeterminateMessageCount,
            nonPrivmsg,
            malformed,
            _outsideDayCount,
            new Dictionary<string, int>(_humanCounts, StringComparer.Ordinal),
            new Dictionary<string, int>(_botCounts, StringComparer.Ordinal),
            new Dictionary<string, int>(_sharedChatCounts, StringComparer.Ordinal),
            new Dictionary<string, int>(_unmatchedByReason, StringComparer.Ordinal),
            _firstSeenUnknownHits,
            histogram,
            cellCount,
            distinctChatters);
    }

    /// <summary>
    /// Walks the message a second time to name the reason for every token that did not count. The
    /// tokens are deduplicated per message, the same way <see cref="EmoteNameMatching"/>
    /// deduplicates hits, so a repeated word does not inflate the diagnostics. Empty tokens (double
    /// spaces) are skipped: they are not names.
    /// <para>
    /// <see cref="UnmatchedReason.UnknownName"/> therefore counts every ordinary chat word as well.
    /// That is intentional — the number is only meaningful next to the token volume, not on its own.
    /// </para>
    /// </summary>
    private void ClassifyTokens(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        HashSet<string>? seen = null;
        foreach (var token in text.Split(' '))
        {
            if (token.Length == 0)
            {
                continue;
            }

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(token))
            {
                continue;
            }

            if (_dayMap.ContainsKey(token))
            {
                if (_dayAmbiguousNames.Contains(token))
                {
                    Mark(UnmatchedReason.AmbiguousName);
                }

                continue;
            }

            // The token missed the day map. If the channel-wide map knows the name, its coalesced
            // emote cannot cover the day (otherwise the name would be in the day map), so the only
            // question left is on which side of the day it lies. "Archived without a date" has no
            // side and is treated as archived — the final report counts those emotes separately.
            if (_channelMap.TryGetValue(token, out var emoteId) && _emotesById.TryGetValue(emoteId, out var emote))
            {
                Mark(emote.FirstSeenAt is { } firstSeenAt && DateOnly.FromDateTime(firstSeenAt) > _day
                    ? UnmatchedReason.BeforeFirstSeen
                    : UnmatchedReason.AfterArchived);
                continue;
            }

            Mark(UnmatchedReason.UnknownName);
        }
    }

    private void Mark(UnmatchedReason reason)
    {
        var key = ReasonNames[(int)reason];
        _unmatchedByReason[key] = _unmatchedByReason.GetValueOrDefault(key) + 1;
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException(
                "Der Tageszähler wurde bereits abgeschlossen; je Archivtag ist genau eine Instanz zu benutzen.");
        }
    }

    /// <summary>
    /// Whether an emote may be hit on <paramref name="day"/>: added no later than the day, archived
    /// no earlier than the day. An emote flagged archived without an archive date is excluded
    /// altogether — the live path stopped counting it at an unknown point in time, so any assumption
    /// here would be invented.
    /// </summary>
    private static bool CoversDay(ReplayEmote emote, DateOnly day)
    {
        if (emote.IsArchived && emote.ArchivedAt is null)
        {
            return false;
        }

        if (emote.FirstSeenAt is { } firstSeenAt && DateOnly.FromDateTime(firstSeenAt) > day)
        {
            return false;
        }

        return emote.ArchivedAt is not { } archivedAt || DateOnly.FromDateTime(archivedAt) >= day;
    }
}
