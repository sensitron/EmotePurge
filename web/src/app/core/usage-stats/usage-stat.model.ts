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

/** One emote inside {@link ChannelUsageSeries}: `[dayOffset, useCount]` pairs, ascending. */
export interface EmoteSeriesEntry {
  emoteId: string;
  /** Offsets count days from the response's `from`; only days with usage are present. */
  days: [number, number][];
}

/**
 * GET /api/channels/{c}/usage-stats/series — every unarchived emote's days in one response, so the
 * atlas can draw a curve for whichever emote the pointer is on without asking per emote.
 *
 * Offsets instead of ISO dates and omitted emotes instead of zero-filled ones: this response covers
 * a whole set where {@link EmoteUsageSeries} covers one emote, and nothing between the Api and the
 * browser compresses JSON. An emote missing from `emotes` had no usage in the range.
 */
export interface ChannelUsageSeries {
  /** The requested range, both inclusive, echoed back. */
  from: string;
  to: string;
  /** Day offsets from `from` with live coverage — same "absent means unknown" caveat as above. */
  liveDays: number[];
  emotes: EmoteSeriesEntry[];
}
