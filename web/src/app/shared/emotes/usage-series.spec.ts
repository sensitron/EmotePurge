import { describe, expect, it } from 'vitest';

import {
  fillDailySeries,
  fillOffsetSeries,
  liveBands,
  offsetsToDates,
  seriesPeak,
  toPolylinePoints,
} from './usage-series';

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

describe('fillOffsetSeries', () => {
  it('places each pair at its offset and zero-fills the rest', () => {
    const points = fillOffsetSeries(
      [
        [0, 3],
        [2, 5],
      ],
      '2026-07-01',
      '2026-07-04',
    );
    // Identical output to fillDailySeries on the same data — the two encodings are the same series.
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 3 },
      { date: '2026-07-02', useCount: 0 },
      { date: '2026-07-03', useCount: 5 },
      { date: '2026-07-04', useCount: 0 },
    ]);
  });

  it('drops offsets outside the range rather than growing the array', () => {
    const points = fillOffsetSeries(
      [
        [-1, 99],
        [1, 4],
        [7, 99],
      ],
      '2026-07-01',
      '2026-07-02',
    );
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 0 },
      { date: '2026-07-02', useCount: 4 },
    ]);
  });

  it('returns [] for an inverted or invalid range', () => {
    expect(fillOffsetSeries([[0, 1]], '2026-07-05', '2026-07-01')).toEqual([]);
    expect(fillOffsetSeries([[0, 1]], 'nonsense', '2026-07-01')).toEqual([]);
  });
});

describe('offsetsToDates', () => {
  it('counts days from the range start, across a month boundary', () => {
    expect(offsetsToDates([0, 1, 31], '2026-07-31')).toEqual([
      '2026-07-31',
      '2026-08-01',
      '2026-08-31',
    ]);
  });

  it('returns [] for an invalid start date', () => {
    expect(offsetsToDates([0], 'nonsense')).toEqual([]);
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

  it('leaves out the days before drawFrom without moving the rest', () => {
    const points = fillDailySeries(
      [
        { date: '2026-07-03', useCount: 10 },
        { date: '2026-07-04', useCount: 5 },
      ],
      '2026-07-01',
      '2026-07-05',
    );
    // 5 points over width 100 → stepX 25, so the 03. sits at x=50 with or without drawFrom. That is
    // the whole point: the curve must stay aligned with the live bands underneath it, which keep
    // spanning the full range.
    expect(toPolylinePoints(points, 100, 40, '2026-07-03')).toBe('50,0 75,20 100,40');
  });

  it('changes nothing when drawFrom lies before the range', () => {
    const points = fillDailySeries(
      [{ date: '2026-07-02', useCount: 8 }],
      '2026-07-01',
      '2026-07-03',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-06-01')).toBe(toPolylinePoints(points, 100, 40));
  });

  it('draws nothing when drawFrom lies after the range', () => {
    const points = fillDailySeries(
      [{ date: '2026-07-02', useCount: 8 }],
      '2026-07-01',
      '2026-07-03',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-08-01')).toBe('');
  });

  it('gives a single visible day one day-step instead of the full width', () => {
    // The emote added today: one drawable day. The old single-point branch paints the full width,
    // which would claim the whole range again — the exact statement this change removes.
    const points = fillDailySeries(
      [{ date: '2026-07-05', useCount: 3 }],
      '2026-07-01',
      '2026-07-05',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('87.5,0 100,0');
  });

  it('keeps a single unused visible day on the baseline', () => {
    const points = fillDailySeries([], '2026-07-01', '2026-07-05');
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('87.5,40 100,40');
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

  it('ignores the days before drawFrom, so the axis matches the curve', () => {
    // In practice the leading stretch is all zeroes, so this changes no pixel today. It is here to
    // keep the y-axis label and the drawn line reading the same set of days on purpose rather than
    // by accident.
    const points = [
      { date: '2026-07-01', useCount: 90 },
      { date: '2026-07-02', useCount: 4 },
    ];
    expect(seriesPeak(points, '2026-07-02')).toEqual({ useCount: 4, date: '2026-07-02' });
  });
});
