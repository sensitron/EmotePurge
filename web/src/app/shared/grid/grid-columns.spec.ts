import { describe, expect, it } from 'vitest';

import { chunkIntoRows, computeGridColumns, isCompactViewport } from './grid-columns';

describe('computeGridColumns', () => {
  it.each([
    [1536, 8],
    [2000, 8],
    [1280, 7],
    [1400, 7],
    [1024, 5],
    [1100, 5],
    [768, 4],
    [900, 4],
    [640, 3],
    [700, 3],
    [639, 2],
    [320, 2],
    [0, 2],
  ])('maps width %i to %i columns', (width, expected) => {
    expect(computeGridColumns(width)).toBe(expected);
  });
});

describe('isCompactViewport', () => {
  it.each([
    [359, true],
    [639, true],
    [640, false],
    [1280, false],
  ])('maps width %i to %s', (width, expected) => {
    expect(isCompactViewport(width)).toBe(expected);
  });

  it('shares its boundary with the 2-to-3 column step, so CSS (sm:) and JS agree', () => {
    expect(computeGridColumns(639)).toBe(2);
    expect(isCompactViewport(639)).toBe(true);
    expect(computeGridColumns(640)).toBe(3);
    expect(isCompactViewport(640)).toBe(false);
  });
});

describe('chunkIntoRows', () => {
  it('splits a flat list into fixed-size rows', () => {
    expect(chunkIntoRows([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
  });

  it('returns one row containing everything when columns exactly matches the length', () => {
    expect(chunkIntoRows([1, 2, 3], 3)).toEqual([[1, 2, 3]]);
  });

  it('returns an empty array for an empty list', () => {
    expect(chunkIntoRows([], 4)).toEqual([]);
  });

  it('falls back to a single row when columns is zero or negative', () => {
    expect(chunkIntoRows([1, 2, 3], 0)).toEqual([[1, 2, 3]]);
    expect(chunkIntoRows([1, 2, 3], -1)).toEqual([[1, 2, 3]]);
  });
});
