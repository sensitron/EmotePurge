/**
 * Weight classes for the emote atlas.
 *
 * The bands answer "what does this set actually run on" before anyone judges a single emote, and
 * they are derived from the set itself rather than from fixed thresholds. A fixed "1000+ is heavy"
 * cut is meaningful for HandOfBlood and meaningless for a channel whose busiest emote saw 40 hits —
 * there every emote would land in the same band and the grouping would carry no information at all.
 *
 * So the split is a Pareto one: the emotes that together make up the first half of all usage are
 * the ones carrying the set, the next slice up to 80 % is the regular cast, whatever still has a
 * single hit is the long tail, and zero is zero. That holds at any scale, never produces an
 * all-in-one-band degenerate view on a real set, and states something worth knowing on its own
 * ("eleven emotes are half your chat").
 */
export type UsageBandKey = 'heavy' | 'regular' | 'rare' | 'dead';

/** Fixed render order — heaviest first, dead last. The user scans down towards the candidates. */
export const USAGE_BAND_ORDER: readonly UsageBandKey[] = ['heavy', 'regular', 'rare', 'dead'];

/**
 * The two counts that separate the three non-empty bands, expressed as usage values rather than as
 * positions: a value cut keeps emotes with identical counts together, which a rank cut would split
 * arbitrarily in the middle of a tie.
 */
export interface UsageBandThresholds {
  /** `count >= heavyMin` → `heavy`. Always ≥ 1, so a zero-usage emote can never be heavy. */
  heavyMin: number;
  /** `count >= regularMin` → `regular`. Never above `heavyMin`. */
  regularMin: number;
}

const HEAVY_SHARE = 0.5;
const REGULAR_SHARE = 0.8;

/**
 * Derives the cuts from every count in the current set. Order of the input does not matter — the
 * function sorts its own copy, so it can be fed the display list in whatever order the user chose.
 */
export function usageBandThresholds(counts: readonly number[]): UsageBandThresholds {
  const used = counts.filter((count) => count > 0).sort((a, b) => b - a);
  const total = used.reduce((sum, count) => sum + count, 0);
  if (total === 0) {
    // No usage anywhere: every emote is dead, and both cuts sit above any possible count so that
    // usageBandOf() cannot accidentally promote a 0 into a live band.
    return { heavyMin: Number.POSITIVE_INFINITY, regularMin: Number.POSITIVE_INFINITY };
  }

  let cumulative = 0;
  let heavyMin = used[used.length - 1];
  let regularMin = used[used.length - 1];
  let heavyFound = false;
  for (const count of used) {
    cumulative += count;
    if (!heavyFound && cumulative >= total * HEAVY_SHARE) {
      heavyMin = count;
      heavyFound = true;
    }
    if (cumulative >= total * REGULAR_SHARE) {
      regularMin = count;
      break;
    }
  }

  // A set whose whole usage sits in one emote makes both cuts land on the same value; the regular
  // band is then simply empty, which the caller renders by leaving the header out.
  return {
    heavyMin: Math.max(heavyMin, 1),
    regularMin: Math.max(Math.min(regularMin, heavyMin), 1),
  };
}

export function usageBandOf(count: number, thresholds: UsageBandThresholds): UsageBandKey {
  if (count <= 0) {
    return 'dead';
  }
  if (count >= thresholds.heavyMin) {
    return 'heavy';
  }
  return count >= thresholds.regularMin ? 'regular' : 'rare';
}

/**
 * Groups an already-sorted list into the four bands without reordering within a band.
 *
 * The band is a property of the emote's weight, the order inside it is whatever the user picked in
 * the toolbar — sorting by "last used" therefore still lists the heavy emotes first, just ordered
 * by date among themselves. Empty bands are dropped rather than rendered as a header with nothing
 * under it.
 */
export function groupIntoUsageBands<T>(
  items: readonly T[],
  count: (item: T) => number,
  thresholds: UsageBandThresholds,
): { key: UsageBandKey; items: T[]; peak: number }[] {
  const buckets = new Map<UsageBandKey, T[]>();
  for (const item of items) {
    const key = usageBandOf(count(item), thresholds);
    const bucket = buckets.get(key);
    if (bucket) {
      bucket.push(item);
    } else {
      buckets.set(key, [item]);
    }
  }

  return USAGE_BAND_ORDER.filter((key) => buckets.has(key)).map((key) => {
    const bandItems = buckets.get(key) ?? [];
    return { key, items: bandItems, peak: Math.max(1, ...bandItems.map(count)) };
  });
}

/**
 * Width of a cell's fill bar, in percent of the cell.
 *
 * Relative to the band's own peak rather than to the set's, because within a band the interesting
 * comparison is against the neighbours — measured against a 9.800-hit leader every bar in the rare
 * band would be a single invisible pixel. Square-rooted for the same reason bar charts of long-tail
 * data usually are: a linear scale spends its whole range on the top few and flattens everything
 * else into nothing. Floored at 6 % so a live emote never looks like a dead one.
 */
export function usageFillPercent(count: number, bandPeak: number): number {
  if (count <= 0) {
    return 0;
  }
  return Math.max(6, Math.min(100, Math.sqrt(count / Math.max(bandPeak, 1)) * 100));
}

/**
 * Condenses the ranked usage curve into a fixed number of buckets for the distribution strip.
 *
 * Per-emote bars were the first attempt and do not survive contact with a real set: 900 bars in
 * 990 px is a smear, not a curve. Bucketing keeps the shape — the steep head, the knee, the flat
 * dead tail — readable at any width and at any set size, and the returned values are already
 * normalised to 0…1 so the template only multiplies by a height.
 */
export function usageDistribution(counts: readonly number[], buckets: number): number[] {
  const ranked = [...counts].sort((a, b) => b - a);
  if (ranked.length === 0 || buckets <= 0) {
    return [];
  }

  // Never more buckets than emotes: upsampling would repeat the same rank across several bars and
  // draw a staircase that claims a resolution the data does not have. A small set simply gets one
  // bar per emote, stretched across the strip by the layout.
  const width = Math.min(buckets, ranked.length);
  const size = ranked.length / width;
  const means: number[] = [];
  for (let i = 0; i < width; i++) {
    const start = Math.floor(i * size);
    const slice = ranked.slice(start, Math.max(Math.floor((i + 1) * size), start + 1));
    means.push(slice.reduce((sum, count) => sum + count, 0) / slice.length);
  }

  const peak = Math.max(...means);
  if (peak <= 0) {
    return means.map(() => 0);
  }
  // Logarithmic, not linear and not square-rooted. Measured on a real 900-emote curve: linear draws
  // one spike and a floor, and even a square root leaves everything past the first tenth under four
  // pixels — both render as decoration. A log scale is what puts the knee of the curve and the flat
  // dead tail on screen as distinguishable shapes, which is the only reason the strip is here.
  const ceiling = Math.log1p(peak);
  return means.map((mean) => (mean <= 0 ? 0 : Math.log1p(mean) / ceiling));
}

/** Share of the total usage carried by the busiest fifth — the set's concentration in one number. */
export function topFifthShare(counts: readonly number[]): number | null {
  const ranked = [...counts].sort((a, b) => b - a);
  const total = ranked.reduce((sum, count) => sum + count, 0);
  if (total === 0) {
    return null;
  }
  const head = ranked.slice(0, Math.max(1, Math.round(ranked.length * 0.2)));
  return head.reduce((sum, count) => sum + count, 0) / total;
}
