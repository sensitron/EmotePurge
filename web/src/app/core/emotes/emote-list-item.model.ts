/**
 * One active (non-archived) emote of a channel's current 7TV set, reduced to what the import
 * dialog needs to answer "already in the target set?" and "name collision?" — no usage numbers,
 * no time range, no internal `Emote.Id` (channel-scoped, meaningless across channels).
 * Served by `GET /api/channels/{channel}/emotes`.
 */
export interface EmoteListItem {
  sevenTvEmoteId: string;
  name: string;
}
