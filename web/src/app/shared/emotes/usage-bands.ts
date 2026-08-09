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
 * Fill class per band, for the distribution strip, its segment bar and the sheet's band headers.
 *
 * A brightness ramp of the one accent colour rather than four hues: the bands are a ranking, not
 * categories, and a second hue would claim a difference in kind that is not there. No new colour
 * token is involved — these are opacity steps of colours that already exist.
 *
 * Written out as whole literals because Tailwind scans source text: a class assembled at runtime
 * from a prefix and a variable never reaches the generated stylesheet.
 */
export const USAGE_BAND_FILL: Record<UsageBandKey, string> = {
  heavy: 'bg-accent-fg',
  regular: 'bg-accent-fg/55',
  rare: 'bg-accent-fg/25',
  dead: 'bg-fg-disabled/40',
};

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
 *
 * `totalUsage` is handed in rather than summed from `items` because the two are not the same
 * question: `items` is the filtered view, while the share a band carries is only meaningful against
 * the usage of the whole set. Passing the set's total keeps "52 % of usage · 4 emotes" true when
 * nothing is filtered and honest when something is — a name filter then shows a smaller share next
 * to a smaller count instead of claiming 52 % for the three emotes still on screen.
 */
export function groupIntoUsageBands<T>(
  items: readonly T[],
  count: (item: T) => number,
  thresholds: UsageBandThresholds,
  totalUsage: number,
): { key: UsageBandKey; items: T[]; peak: number; usage: number; share: number }[] {
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
    const usage = bandItems.reduce((sum, item) => sum + Math.max(0, count(item)), 0);
    return {
      key,
      items: bandItems,
      peak: Math.max(1, ...bandItems.map(count)),
      usage,
      share: totalUsage > 0 ? usage / totalUsage : 0,
    };
  });
}

/**
 * Which band each bar of the distribution strip belongs to.
 *
 * Derived from the band sizes rather than resolved per bar: the bars are equal slices of the
 * ranking, so the two agree to within one bar and the cheap form is the one worth having. Feed it
 * the counts of the WHOLE set — the strip draws the set, not the filtered view.
 *
 * Every non-empty band gets at least one bar. On a concentrated set the heavy band is four emotes
 * in seven hundred and rounds to zero, and the one band the strip most needs to show would be the
 * one it drops. Where that padding pushes the total past `bars`, the tail is cut instead.
 */
export function usageBandBars(
  counts: readonly number[],
  thresholds: UsageBandThresholds,
  bars: number,
): UsageBandKey[] {
  if (bars <= 0 || counts.length === 0) {
    return [];
  }

  const sizes = new Map<UsageBandKey, number>();
  for (const count of counts) {
    const key = usageBandOf(count, thresholds);
    sizes.set(key, (sizes.get(key) ?? 0) + 1);
  }

  const out: UsageBandKey[] = [];
  for (const key of USAGE_BAND_ORDER) {
    const size = sizes.get(key) ?? 0;
    if (size === 0) {
      continue;
    }
    const width = Math.max(1, Math.round((size / counts.length) * bars));
    for (let i = 0; i < width && out.length < bars; i++) {
      out.push(key);
    }
  }

  // Rounding down across several bands can leave the strip short of a full row; the last band
  // absorbs the remainder, which is the dead tail on any real set.
  while (out.length < bars) {
    out.push(out[out.length - 1]);
  }
  return out;
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
