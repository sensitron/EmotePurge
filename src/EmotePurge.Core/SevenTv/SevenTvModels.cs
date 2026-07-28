namespace EmotePurge.Core.SevenTv;

public record SevenTvEmote(string Id, string Name, string ImageUrl);

public record SevenTvEmoteSet(string Id, IReadOnlyList<SevenTvEmote> Emotes);

// A 7TV account's own identity plus its currently active Twitch-linked emote set, resolved together
// in one GQL call (userByConnection) — reused both to find "is this my own set" (owner comparison)
// and, per moderated channel, "what's currently active there" (shared-set detection).
public record SevenTvIdentity(string SevenTvUserId, string? ActiveEmoteSetId);

// One entry in a 7TV user's editor_of list, reduced to the Twitch identity of the channel they can
// edit — the 7TV-internal user id of the owner isn't needed by any current consumer.
public record SevenTvEditorGrant(string TwitchChannelLogin, string TwitchChannelId);
