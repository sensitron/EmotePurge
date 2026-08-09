import { describe, expect, it } from 'vitest';

import {
  groupIntoUsageBands,
  topFifthShare,
  usageBandBars,
  usageBandOf,
  usageBandThresholds,
  usageDistribution,
  usageFillPercent,
} from './usage-bands';

describe('usageBandThresholds', () => {
  it('puts every emote in the dead band when nothing was used', () => {
    const thresholds = usageBandThresholds([0, 0, 0]);

    expect(usageBandOf(0, thresholds)).toBe('dead');
    // The guard that matters: no count can slip into a live band through an accidental
    // "0 >= 0" comparison.
    expect(usageBandOf(1, thresholds)).toBe('rare');
  });

  it('handles an empty set', () => {
    expect(() => usageBandThresholds([])).not.toThrow();
    expect(usageBandOf(0, usageBandThresholds([]))).toBe('dead');
  });

  it('cuts a long-tail set at the halfway and the 80 % marks', () => {
    // 100 + 60 = 160 of 200 total. Half (100) is reached by the first emote alone, 80 % (160) by
    // the second — so 100 is the heavy cut and 60 the regular one.
    const thresholds = usageBandThresholds([100, 60, 20, 15, 5, 0, 0]);

    expect(usageBandOf(100, thresholds)).toBe('heavy');
    expect(usageBandOf(60, thresholds)).toBe('regular');
    expect(usageBandOf(20, thresholds)).toBe('rare');
    expect(usageBandOf(0, thresholds)).toBe('dead');
  });

  it('does not care about the order it is given', () => {
    const ascending = usageBandThresholds([0, 5, 15, 20, 60, 100]);
    const descending = usageBandThresholds([100, 60, 20, 15, 5, 0]);

    expect(ascending).toEqual(descending);
  });

  it('keeps equal counts in the same band rather than splitting a tie', () => {
    // Four identical counts: whichever one the cumulative sum happens to cross on, all four have
    // to land together — a rank-based cut would put two on either side.
    const thresholds = usageBandThresholds([10, 10, 10, 10]);

    expect(usageBandOf(10, thresholds)).toBe('heavy');
  });

  it('survives a set whose whole usage sits in one emote', () => {
    const thresholds = usageBandThresholds([50, 0, 0]);

    expect(usageBandOf(50, thresholds)).toBe('heavy');
    expect(usageBandOf(0, thresholds)).toBe('dead');
  });
});

describe('groupIntoUsageBands', () => {
  const item = (name: string, uses: number) => ({ name, uses });
  const count = (entry: { uses: number }) => entry.uses;

  it('emits the bands heaviest first and drops the empty ones', () => {
    const items = [item('a', 100), item('b', 60), item('c', 0)];
    const bands = groupIntoUsageBands(items, count, usageBandThresholds(items.map(count)), 160);

    expect(bands.map((band) => band.key)).toEqual(['heavy', 'regular', 'dead']);
  });

  it('preserves the incoming order inside a band', () => {
    // The band is the emote's weight class; the order within it is whatever the user sorted by —
    // here "last used" would hand over a heavy band that is NOT usage-ordered.
    const items = [item('b', 40), item('a', 60), item('c', 0)];
    const bands = groupIntoUsageBands(items, count, { heavyMin: 30, regularMin: 10 }, 100);

    expect(bands[0].items.map((entry) => entry.name)).toEqual(['b', 'a']);
  });

  it('reports each band its own peak', () => {
    const items = [item('a', 100), item('b', 80), item('c', 5), item('d', 3)];
    const bands = groupIntoUsageBands(items, count, { heavyMin: 50, regularMin: 50 }, 188);

    expect(bands[0].peak).toBe(100);
    expect(bands[1].peak).toBe(5);
  });

  it('returns nothing for an empty list', () => {
    expect(groupIntoUsageBands([], count, { heavyMin: 1, regularMin: 1 }, 0)).toEqual([]);
  });

  it('reports the share each band carries of the whole set', () => {
    const items = [item('a', 100), item('b', 60), item('c', 40), item('d', 0)];
    const bands = groupIntoUsageBands(items, count, { heavyMin: 100, regularMin: 40 }, 200);

    expect(bands.map((band) => band.usage)).toEqual([100, 100, 0]);
    expect(bands.map((band) => band.share)).toEqual([0.5, 0.5, 0]);
  });

  it('measures a filtered view against the whole set, not against itself', () => {
    // Same total as above, but only two of the four emotes survived a name filter. The heavy band
    // must not claim the full 50 % it carries in the unfiltered set.
    const visible = [item('b', 60), item('d', 0)];
    const bands = groupIntoUsageBands(visible, count, { heavyMin: 100, regularMin: 40 }, 200);

    expect(bands.map((band) => band.key)).toEqual(['regular', 'dead']);
    expect(bands[0].share).toBe(0.3);
  });

  it('reports a zero share instead of NaN when nothing was used', () => {
    const items = [item('a', 0), item('b', 0)];
    const bands = groupIntoUsageBands(items, count, { heavyMin: 1, regularMin: 1 }, 0);

    expect(bands[0].share).toBe(0);
  });
});

describe('usageBandBars', () => {
  const thresholds = { heavyMin: 100, regularMin: 10 };

  it('gives every non-empty band at least one bar', () => {
    // The shape of a real set: one heavy emote in fifty. Proportionally it rounds to zero bars,
    // and the band the strip most needs to show would vanish.
    const counts = [500, ...Array.from({ length: 20 }, () => 20), ...Array(29).fill(0)];
    const bars = usageBandBars(counts, thresholds, 20);

    expect(bars.filter((key) => key === 'heavy')).toHaveLength(1);
    expect(new Set(bars)).toEqual(new Set(['heavy', 'regular', 'dead']));
  });

  it('draws exactly the requested number of bars', () => {
    const counts = [500, 300, 40, 20, 5, 0, 0, 0];

    expect(usageBandBars(counts, thresholds, 96)).toHaveLength(96);
    expect(usageBandBars(counts, thresholds, 7)).toHaveLength(7);
  });

  it('keeps the bands in render order', () => {
    const counts = [500, 300, 40, 20, 5, 0, 0, 0];
    const bars = usageBandBars(counts, thresholds, 40);
    const firstOf = (key: string) => bars.indexOf(key as (typeof bars)[number]);

    expect(firstOf('heavy')).toBeLessThan(firstOf('regular'));
    expect(firstOf('regular')).toBeLessThan(firstOf('rare'));
    expect(firstOf('rare')).toBeLessThan(firstOf('dead'));
  });

  it('cuts the tail rather than overrunning when there are more bands than bars', () => {
    const counts = [500, 300, 40, 20, 5, 0, 0, 0];
    const bars = usageBandBars(counts, thresholds, 3);

    expect(bars).toEqual(['heavy', 'regular', 'rare']);
  });

  it('returns nothing for an empty set or a strip with no bars', () => {
    expect(usageBandBars([], thresholds, 96)).toEqual([]);
    expect(usageBandBars([1, 2, 3], thresholds, 0)).toEqual([]);
  });
});

describe('usageFillPercent', () => {
  it('draws nothing for a never-used emote', () => {
    expect(usageFillPercent(0, 100)).toBe(0);
  });

  it('fills the cell at the band peak', () => {
    expect(usageFillPercent(100, 100)).toBe(100);
  });

  it('keeps the faintest live emote visible', () => {
    // Square-rooted, 1/10000 would be 1 % — below the floor, so a single hit still shows a mark.
    expect(usageFillPercent(1, 10000)).toBe(6);
  });

  it('spreads the mid range instead of collapsing it', () => {
    // A linear scale would put a quarter-peak emote at 25 %; the square root lifts it to 50 %,
    // which is the whole point of the scale on long-tail data.
    expect(usageFillPercent(25, 100)).toBe(50);
  });

  it('survives a zero peak', () => {
    expect(usageFillPercent(5, 0)).toBeLessThanOrEqual(100);
  });
});

describe('usageDistribution', () => {
  it('never draws more bars than there are emotes', () => {
    expect(usageDistribution([5, 3, 1], 96)).toHaveLength(3);
  });

  it('condenses a large set into the requested bucket count', () => {
    const counts = Array.from({ length: 900 }, (_, i) => 900 - i);

    expect(usageDistribution(counts, 96)).toHaveLength(96);
  });

  it('normalises to a peak of 1 and descends', () => {
    const curve = usageDistribution([100, 50, 10, 1], 4);

    expect(curve[0]).toBe(1);
    expect(curve[0]).toBeGreaterThan(curve[1]);
    expect(curve[1]).toBeGreaterThan(curve[2]);
    expect(curve[3]).toBeGreaterThan(0);
  });

  it('flattens to zero when nothing was used', () => {
    expect(usageDistribution([0, 0, 0], 3)).toEqual([0, 0, 0]);
  });

  it('returns nothing for an empty set', () => {
    expect(usageDistribution([], 96)).toEqual([]);
  });

  it('does not care about the incoming order', () => {
    expect(usageDistribution([1, 100, 10], 3)).toEqual(usageDistribution([100, 10, 1], 3));
  });
});

describe('topFifthShare', () => {
  it('is null when there is no usage to share out', () => {
    expect(topFifthShare([0, 0])).toBeNull();
    expect(topFifthShare([])).toBeNull();
  });

  it('measures the concentration of a flat set at a fifth', () => {
    const flat = Array.from({ length: 10 }, () => 10);

    expect(topFifthShare(flat)).toBeCloseTo(0.2);
  });

  it('reports a concentrated set as concentrated', () => {
    const skewed = [900, ...Array.from({ length: 9 }, () => 10)];

    expect(topFifthShare(skewed)).toBeGreaterThan(0.9);
  });

  it('always counts at least one emote, even in a set of one', () => {
    expect(topFifthShare([42])).toBe(1);
  });
});
