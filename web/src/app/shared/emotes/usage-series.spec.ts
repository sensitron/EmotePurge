import { describe, expect, it } from 'vitest';

import { fillDailySeries, liveBands, seriesPeak, toPolylinePoints } from './usage-series';

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

describe('liveBands', () => {
  const week = fillDailySeries([], '2026-07-01', '2026-07-07');

  it('merges consecutive live days into one band spanning their half-steps', () => {
    // 7 points over width 100 → stepX 100/6. Days at index 1..2 → from 0.5 to 2.5 steps.
    const bands = liveBands(week, ['2026-07-02', '2026-07-03'], 100);
    expect(bands).toEqual([{ x: 8.33, width: 33.33 }]);
  });

  it('keeps separate runs as separate bands and clamps to the viewBox edges', () => {
    const bands = liveBands(week, ['2026-07-01', '2026-07-06', '2026-07-07'], 100);
    expect(bands).toEqual([
      { x: 0, width: 8.33 },
      { x: 75, width: 25 },
    ]);
  });

  it('ignores live days outside the rendered range', () => {
    expect(liveBands(week, ['2026-06-30', '2026-08-01'], 100)).toEqual([]);
  });

  it('marks a single-point range as one full-width band', () => {
    const day = fillDailySeries([], '2026-07-01', '2026-07-01');
    expect(liveBands(day, ['2026-07-01'], 100)).toEqual([{ x: 0, width: 100 }]);
    expect(liveBands(day, ['2026-07-02'], 100)).toEqual([]);
  });

  it('returns no bands without points or without live days', () => {
    expect(liveBands([], ['2026-07-01'], 100)).toEqual([]);
    expect(liveBands(week, [], 100)).toEqual([]);
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
