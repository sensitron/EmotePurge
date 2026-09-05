namespace EmotePurge.Core.Matching;

/// <summary>
/// The one emote-name matching rule shared by the live chat path
/// (<c>TwitchChatManager.OnMessageReceived</c>), the live match-cache build
/// (<c>SevenTvSyncService.RefreshMatchCacheAsync</c>) and the chat-log backfill harness (#69).
/// <para>
/// The rule is deliberately naive — split on plain spaces, ordinal lookup, no trimming, no
/// case-folding, no Twitch emote tags — and is not a place to fix anything: the harness exists to
/// measure whether an import reproduces the live count, and a "better" rule here would change what
/// is being measured on both sides at once. See the DECISIONS.md entry for this file.
/// </para>
/// </summary>
public static class EmoteNameMatching
{
    private static readonly IReadOnlySet<string> EmptyMatches = new HashSet<string>();
    private static readonly IReadOnlySet<string> EmptyAmbiguousNames = new HashSet<string>();

    /// <summary>
    /// Splits <paramref name="message"/> on single spaces and looks up every token ordinally in
    /// <paramref name="nameToId"/>, returning the set of matched emote ids deduplicated per
    /// message — i.e. "messages using this emote", not "occurrences". An empty message or an
    /// empty map returns a shared empty instance without allocating.
    /// </summary>
    public static IReadOnlySet<string> MatchEmoteIds(string message, IReadOnlyDictionary<string, string> nameToId)
    {
        if (message.Length == 0 || nameToId.Count == 0)
        {
            return EmptyMatches;
        }

        // Mirrors the hot path this replaces: exactly one array from Split and one HashSet, no
        // LINQ, no closure, no boxed enumerator.
        var matched = new HashSet<string>();
        foreach (var token in message.Split(' '))
        {
            if (nameToId.TryGetValue(token, out var emoteId))
            {
                matched.Add(emoteId);
            }
        }

        return matched;
    }

    /// <summary>
    /// Coalesces emotes sharing the same chat name onto the first one encountered in
    /// <paramref name="emotesInLoadOrder"/>, since the same name can legitimately be active on two
    /// emotes at once (observed live). The winner is therefore whatever order the caller loaded
    /// its emotes in — for the live match cache that is an unspecified query order without an
    /// <c>OrderBy</c>, which this shared rule preserves rather than fixes; the harness surfaces
    /// that instead of papering over it.
    /// </summary>
    public static EmoteNameMap Coalesce(IEnumerable<KeyValuePair<string, string>> emotesInLoadOrder)
    {
        var nameToId = new Dictionary<string, string>();
        HashSet<string>? ambiguousNames = null;

        foreach (var (name, id) in emotesInLoadOrder)
        {
            if (!nameToId.TryAdd(name, id))
            {
                (ambiguousNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
            }
        }

        return new EmoteNameMap(nameToId, (IReadOnlySet<string>?)ambiguousNames ?? EmptyAmbiguousNames);
    }
}

/// <summary>
/// Result of <see cref="EmoteNameMatching.Coalesce"/>: the winning name-to-id map (first entry per
/// name) plus every name that collided along the way.
/// </summary>
public readonly record struct EmoteNameMap(Dictionary<string, string> NameToId, IReadOnlySet<string> AmbiguousNames);
