import { describe, expect, it } from 'vitest';

import { fillDailySeries, seriesPeak, toPolylinePoints } from './usage-series';

describe('fillDailySeries', () => {
  it('zero-fills the gaps of a sparse series', () => {
    const points = fillDailySeries(
      [
        { date: '2026-07-01', useCount: 3 },
        { date: '2026-07-03', useCount: 5 },
      ],
      '2026-07-01',
      '2026-07-04',
    );
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 3 },
      { date: '2026-07-02', useCount: 0 },
      { date: '2026-07-03', useCount: 5 },
      { date: '2026-07-04', useCount: 0 },
    ]);
  });

  it('handles a single-day range', () => {
    expect(
      fillDailySeries([{ date: '2026-07-01', useCount: 2 }], '2026-07-01', '2026-07-01'),
    ).toEqual([{ date: '2026-07-01', useCount: 2 }]);
  });

  it('ignores days outside the range', () => {
    const points = fillDailySeries(
      [
        { date: '2026-06-30', useCount: 99 },
        { date: '2026-07-02', useCount: 1 },
      ],
      '2026-07-01',
      '2026-07-02',
    );
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 0 },
      { date: '2026-07-02', useCount: 1 },
    ]);
  });

  it('returns [] for an inverted or invalid range', () => {
    expect(fillDailySeries([], '2026-07-05', '2026-07-01')).toEqual([]);
    expect(fillDailySeries([], 'nonsense', '2026-07-01')).toEqual([]);
  });
});

describe('toPolylinePoints', () => {
  it('spreads the points over the width and inverts the y axis', () => {
    const points = toPolylinePoints(
      [
        { date: '2026-07-01', useCount: 0 },
        { date: '2026-07-02', useCount: 10 },
        { date: '2026-07-03', useCount: 5 },
      ],
      100,
      40,
    );
    expect(points).toBe('0,40 50,0 100,20');
  });

  it('draws an all-zero series as the baseline without dividing by zero', () => {
    const points = toPolylinePoints(
      [
        { date: '2026-07-01', useCount: 0 },
        { date: '2026-07-02', useCount: 0 },
      ],
      100,
      40,
    );
    expect(points).toBe('0,40 100,40');
  });

  it('renders a single point as a full-width flat line', () => {
    expect(toPolylinePoints([{ date: '2026-07-01', useCount: 4 }], 100, 40)).toBe('0,0 100,0');
  });

  it('returns an empty string for no points', () => {
    expect(toPolylinePoints([], 100, 40)).toBe('');
  });
});

describe('seriesPeak', () => {
  it('finds the busiest day', () => {
    const peak = seriesPeak([
      { date: '2026-07-01', useCount: 3 },
      { date: '2026-07-02', useCount: 9 },
      { date: '2026-07-03', useCount: 4 },
    ]);
    expect(peak).toEqual({ useCount: 9, date: '2026-07-02' });
  });

  it('lets the earliest day win a tie', () => {
    const peak = seriesPeak([
      { date: '2026-07-01', useCount: 9 },
      { date: '2026-07-02', useCount: 9 },
    ]);
    expect(peak?.date).toBe('2026-07-01');
  });

  it('returns null for an empty or all-zero series', () => {
    expect(seriesPeak([])).toBeNull();
    expect(seriesPeak([{ date: '2026-07-01', useCount: 0 }])).toBeNull();
  });
});
