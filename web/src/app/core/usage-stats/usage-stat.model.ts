export interface EmoteUsageTotal {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  totalUseCount: number;

  /**
   * ISO date (`yyyy-MM-dd`) of the emote's last use ever — deliberately not bounded by the selected
   * range, so switching to "7 days" does not report the whole set as never used. `null` = never
   * used since tracking began.
   */
  lastUsedDate: string | null;

  /** Uses over the equally long window immediately before the selected range. */
  previousWindowUseCount: number;

  /** ISO timestamp of when the emote entered the 7TV set. `null` = unknown, never "new". */
  firstSeenAt: string | null;
}
