namespace EmotePurge.Core.SevenTv;

public record SevenTvEmote(string Id, string Name, string ImageUrl);

public record SevenTvEmoteSet(string Id, IReadOnlyList<SevenTvEmote> Emotes);

// The channel's currently active set plus the 7TV account behind the Twitch connection, resolved
// together from one users/twitch/{id} REST call. The account id is what the EventAPI's user.*
// subscription needs to detect active-set switches; it is distinct from the set's owner, which can
// be a third party (see GetEmoteSetOwnerIdAsync).
public record SevenTvChannelState(string? SevenTvUserId, SevenTvEmoteSet EmoteSet);

// What a full channel sync resolved. Callers use the set id for logging and the pair to keep an
// EventAPI subscription registry converged after every sync.
public record SevenTvSyncResult(string EmoteSetId, string? SevenTvUserId);

// One emote_set.update dispatch from the EventAPI, reduced to the three change kinds the wire
// actually carries. Property names deliberately mirror the wire fields (pushed/pulled/updated):
// the 2026-07 implementation silently produced empty deltas because it read added/removed —
// field names that do not exist in real dispatches (docs/Untersuchung-7TV-WebSocket-2026-07-30.md).
public record SevenTvEmoteSetDelta(
    IReadOnlyList<SevenTvEmote> Pushed,
    IReadOnlyList<SevenTvEmote> Updated,
    IReadOnlyList<string> PulledIds)
{
    public bool IsEmpty => Pushed.Count == 0 && Updated.Count == 0 && PulledIds.Count == 0;
}

// A user.update dispatch that switched the active emote set of a Twitch connection. Old id is
// null when the wire omits it; new id is null when the connection ends up with no active set.
public record SevenTvUserSetChange(string SevenTvUserId, string? OldEmoteSetId, string? NewEmoteSetId);

// A 7TV account's own identity plus its currently active Twitch-linked emote set, resolved together
// in one GQL call (userByConnection) — reused both to find "is this my own set" (owner comparison)
// and, per moderated channel, "what's currently active there" (shared-set detection).
public record SevenTvIdentity(string SevenTvUserId, string? ActiveEmoteSetId);

// One entry in a 7TV user's editor_of list, reduced to the Twitch identity of the channel they can
// edit — the 7TV-internal user id of the owner isn't needed by any current consumer.
public record SevenTvEditorGrant(string TwitchChannelLogin, string TwitchChannelId);
