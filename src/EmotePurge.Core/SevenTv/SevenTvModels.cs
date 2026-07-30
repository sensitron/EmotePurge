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

// A 7TV account's own identity plus its currently active Twitch-linked emote set, resolved together
// in one GQL call (userByConnection) — reused both to find "is this my own set" (owner comparison)
// and, per moderated channel, "what's currently active there" (shared-set detection).
public record SevenTvIdentity(string SevenTvUserId, string? ActiveEmoteSetId);

// One entry in a 7TV user's editor_of list, reduced to the Twitch identity of the channel they can
// edit — the 7TV-internal user id of the owner isn't needed by any current consumer.
public record SevenTvEditorGrant(string TwitchChannelLogin, string TwitchChannelId);
