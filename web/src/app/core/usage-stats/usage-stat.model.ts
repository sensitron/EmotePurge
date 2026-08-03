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

/** One day with actual usage — days without usage are absent, not zero (see EmoteUsageSeries). */
export interface EmoteDailyUsage {
  /** ISO date, `yyyy-MM-dd`. */
  date: string;
  useCount: number;
}

/** GET /api/channels/{c}/usage-stats/daily — one emote's series for the drilldown (A5). */
export interface EmoteUsageSeries {
  emoteId: string;
  emoteName: string;
  /** The requested range, both inclusive, echoed back. */
  from: string;
  to: string;
  totalUseCount: number;
  /** First/last use ever, deliberately not bounded by the range. `null` = never used. */
  firstUsedDate: string | null;
  lastUsedDate: string | null;
  /** Sparse: only days with usage, ascending — the client zero-fills for rendering. */
  days: EmoteDailyUsage[];
  /**
   * ISO dates (`yyyy-MM-dd`) within [from, to] on which the channel was live, ascending (A10).
   * Coverage data only exists since the worker's live poll shipped — an absent day in an older
   * range means "unknown", not "offline", so the chart marks live days and states nothing else.
   */
  liveDays: string[];
}
