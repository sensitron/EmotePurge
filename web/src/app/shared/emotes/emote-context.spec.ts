import { describe, expect, it } from 'vitest';

import {
  MIN_TREND_SAMPLES,
  OBSERVATION_PERIOD_DAYS,
  TrendInput,
  daysInSet,
  isUnderObservation,
  usageTrend,
} from './emote-context';

const NOW = new Date('2026-08-01T12:00:00Z');

/** A 30-day range with a channel tracked long before it — the "nothing suppresses it" baseline. */
function trend(overrides: Partial<TrendInput> = {}): TrendInput {
  return {
    totalUseCount: 100,
    previousWindowUseCount: 100,
    firstSeenAt: '2026-01-01T00:00:00Z',
    windowStart: '2026-07-02',
    windowEnd: '2026-07-31',
    trackedSince: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('daysInSet', () => {
  it('counts whole days since the emote entered the set', () => {
    expect(daysInSet('2026-07-25T12:00:00Z', NOW)).toBe(7);
  });

  it('returns null for an unknown date', () => {
    expect(daysInSet(null, NOW)).toBeNull();
  });

  it('returns null for an unparseable date', () => {
    expect(daysInSet('not-a-date', NOW)).toBeNull();
  });
});

describe('isUnderObservation', () => {
  it('covers an emote younger than the observation period', () => {
    expect(isUnderObservation('2026-07-25T12:00:00Z', NOW)).toBe(true);
  });

  it('releases an emote exactly at the period boundary', () => {
    const boundary = new Date(NOW.getTime() - OBSERVATION_PERIOD_DAYS * 86_400_000).toISOString();
    expect(isUnderObservation(boundary, NOW)).toBe(false);
  });

  it('holds an emote one day short of the boundary', () => {
    const justInside = new Date(
      NOW.getTime() - (OBSERVATION_PERIOD_DAYS - 1) * 86_400_000,
    ).toISOString();
    expect(isUnderObservation(justInside, NOW)).toBe(true);
  });

  it('never puts an emote with an unknown date under observation', () => {
    // A filter must not hide rows because data is missing — every row predating the column would
    // otherwise disappear from the grid.
    expect(isUnderObservation(null, NOW)).toBe(false);
  });
});

describe('usageTrend', () => {
  it('calls a clear increase rising', () => {
    expect(usageTrend(trend({ totalUseCount: 200, previousWindowUseCount: 100 }))).toBe('rising');
  });

  it('calls a clear decrease falling', () => {
    expect(usageTrend(trend({ totalUseCount: 40, previousWindowUseCount: 100 }))).toBe('falling');
  });

  it('calls a small change stable', () => {
    expect(usageTrend(trend({ totalUseCount: 110, previousWindowUseCount: 100 }))).toBe('stable');
  });

  it('treats the threshold itself as a trend, not as stable', () => {
    expect(usageTrend(trend({ totalUseCount: 125, previousWindowUseCount: 100 }))).toBe('rising');
    expect(usageTrend(trend({ totalUseCount: 75, previousWindowUseCount: 100 }))).toBe('falling');
    expect(usageTrend(trend({ totalUseCount: 124, previousWindowUseCount: 100 }))).toBe('stable');
    expect(usageTrend(trend({ totalUseCount: 76, previousWindowUseCount: 100 }))).toBe('stable');
  });

  it('calls an emote that was unused before rising once there are enough samples', () => {
    expect(usageTrend(trend({ totalUseCount: MIN_TREND_SAMPLES, previousWindowUseCount: 0 }))).toBe(
      'rising',
    );
  });

  it('suppresses the label when the preceding window predates the tracking start', () => {
    // The previous window's zero would be our own gap, not the emote's decline.
    expect(usageTrend(trend({ trackedSince: '2026-06-15T00:00:00Z' }))).toBe('unknown');
  });

  it('suppresses the label when the emote did not exist for the whole preceding window', () => {
    // Without this, every recently added emote reads as "rising" forever — an artefact of its own
    // age, which is exactly what the observation period exists to prevent.
    expect(usageTrend(trend({ firstSeenAt: '2026-06-20T00:00:00Z' }))).toBe('unknown');
  });

  it('suppresses the label below the sample floor', () => {
    expect(usageTrend(trend({ totalUseCount: 4, previousWindowUseCount: 2 }))).toBe('unknown');
  });

  it('states a trend exactly at the sample floor', () => {
    expect(
      usageTrend(trend({ totalUseCount: MIN_TREND_SAMPLES - 2, previousWindowUseCount: 2 })),
    ).toBe('rising');
  });

  it('keeps judging an emote with an unknown entry date', () => {
    // Unknown is not young: suppressing here would blank the label for every row that predates the
    // column, which is most of them right after the deploy.
    expect(
      usageTrend(trend({ firstSeenAt: null, totalUseCount: 200, previousWindowUseCount: 100 })),
    ).toBe('rising');
  });

  it('derives the comparison window from the range, not from today', () => {
    // A 2-day range in the past: the preceding window is the 2 days before it, so a tracking start
    // four days before the range must not suppress the label.
    expect(
      usageTrend(
        trend({
          windowStart: '2026-07-05',
          windowEnd: '2026-07-06',
          trackedSince: '2026-07-01T00:00:00Z',
          totalUseCount: 60,
          previousWindowUseCount: 20,
        }),
      ),
    ).toBe('rising');
  });

  it('suppresses the label for an unparseable range', () => {
    expect(usageTrend(trend({ windowStart: 'nonsense' }))).toBe('unknown');
    expect(usageTrend(trend({ windowEnd: 'nonsense' }))).toBe('unknown');
  });

  it('handles a single-day range without dividing by a zero-length window', () => {
    expect(
      usageTrend(
        trend({
          windowStart: '2026-07-31',
          windowEnd: '2026-07-31',
          totalUseCount: 30,
          previousWindowUseCount: 10,
        }),
      ),
    ).toBe('rising');
  });
});
