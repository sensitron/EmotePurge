/** One of the emotes involved in a name collision. */
export interface DuplicateEmote {
  emoteId: string;
  sevenTvEmoteId: string;
  imageUrl: string;
}

/**
 * An active emote name carried by more than one emote in the channel's current 7TV set.
 * Chat matching is exact (case-sensitive, like Twitch chat), so only exact-name collisions
 * appear here — while one exists, all chat usage of the name is counted onto a single one
 * of the emotes and the others' stats stay empty.
 */
export interface DuplicateEmoteName {
  name: string;
  emotes: DuplicateEmote[];
}
