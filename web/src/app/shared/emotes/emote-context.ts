/**
 * How long a newly added emote is treated as "still under observation" and therefore excluded as a
 * deletion candidate. A streamer going live three times a week needs roughly two weeks before a new
 * emote has had a fair chance to be used; below that, zero uses says nothing about the emote.
 *
 * Lives in the frontend because it only ever affects presentation — a badge and an opt-in filter.
 * There is no server-side "deletion candidates" list, and after the 2026-08-01 voting decision
 * there is deliberately not going to be one. Move it to `Core` the day a server path has to exclude
 * fresh emotes on its own (say, vote-session creation); until then a second copy would be the
 * problem, not the location.
 */
export const OBSERVATION_PERIOD_DAYS = 14;

/**
 * Below this many uses across both windows combined, the direction is noise. Two uses against one
 * is a 100 % rise on paper and means nothing.
 */
export const MIN_TREND_SAMPLES = 10;

/** How far the two windows must differ before the trend is called anything but stable. */
const TREND_THRESHOLD = 0.25;

/** `unknown` = deliberately not stated, never guessed. */
export type UsageTrend = 'rising' | 'stable' | 'falling' | 'unknown';

export interface TrendInput {
  totalUseCount: number;
  previousWindowUseCount: number;
  /** ISO timestamp, `null` when 7TV never reported one. */
  firstSeenAt: string | null;
  /** ISO date (`yyyy-MM-dd`) the selected range starts on. */
  windowStart: string;
  /** ISO date (`yyyy-MM-dd`) the selected range ends on, inclusive. */
  windowEnd: string;
  /** ISO timestamp of when this channel's tracking (re)started. */
  trackedSince: string;
}

/** Whole days between the two instants; negative when `to` precedes `from`. */
export function daysBetween(from: Date, to: Date): number {
  return Math.floor((to.getTime() - from.getTime()) / 86_400_000);
}

/** Days the emote has been in the set, or `null` when 7TV reported no date. */
export function daysInSet(firstSeenAt: string | null, now: Date): number | null {
  if (!firstSeenAt) {
    return null;
  }

  const seen = new Date(firstSeenAt);
  return Number.isNaN(seen.getTime()) ? null : daysBetween(seen, now);
}

/**
 * An emote too young to have earned its usage numbers. Unknown dates are never under observation:
 * a filter must not hide rows because data is missing, and every row predating `FirstSeenAt` would
 * otherwise vanish.
 */
export function isUnderObservation(firstSeenAt: string | null, now: Date): boolean {
  const days = daysInSet(firstSeenAt, now);
  return days !== null && days < OBSERVATION_PERIOD_DAYS;
}

/**
 * Compares the selected window against the equally long one before it.
 *
 * Returns `'unknown'` — a suppressed label, not a guess — in the three cases where the comparison
 * would be an artefact rather than a signal:
 *
 * - the preceding window starts before this channel was being tracked, so its zero is our gap;
 * - the emote did not exist for the whole preceding window, which makes any newcomer read as
 *   "rising" forever;
 * - both windows together hold too few uses to have a direction.
 */
export function usageTrend(input: TrendInput): UsageTrend {
  const windowStart = new Date(`${input.windowStart}T00:00:00Z`);
  const windowEnd = new Date(`${input.windowEnd}T00:00:00Z`);
  if (Number.isNaN(windowStart.getTime()) || Number.isNaN(windowEnd.getTime())) {
    return 'unknown';
  }

  // Derived from the selected range, not from today: with a custom range ending in the past, "now"
  // would stretch the comparison window far beyond the one the server actually summed. Both bounds
  // are inclusive, hence the +1 — the same arithmetic as UsageStatQueryService.
  const windowLengthDays = Math.max(daysBetween(windowStart, windowEnd) + 1, 1);
  const previousWindowStart = new Date(windowStart.getTime() - windowLengthDays * 86_400_000);

  const trackedSince = new Date(input.trackedSince);
  if (Number.isNaN(trackedSince.getTime()) || previousWindowStart < trackedSince) {
    return 'unknown';
  }

  if (input.firstSeenAt) {
    const firstSeen = new Date(input.firstSeenAt);
    if (Number.isNaN(firstSeen.getTime()) || firstSeen > previousWindowStart) {
      return 'unknown';
    }
  }

  const total = input.totalUseCount + input.previousWindowUseCount;
  if (total < MIN_TREND_SAMPLES) {
    return 'unknown';
  }

  // Guarded above: with MIN_TREND_SAMPLES uses in total and none in the previous window, every use
  // is new, which is a rise by any reading.
  if (input.previousWindowUseCount === 0) {
    return 'rising';
  }

  const change =
    (input.totalUseCount - input.previousWindowUseCount) / input.previousWindowUseCount;
  if (change >= TREND_THRESHOLD) {
    return 'rising';
  }
  return change <= -TREND_THRESHOLD ? 'falling' : 'stable';
}
