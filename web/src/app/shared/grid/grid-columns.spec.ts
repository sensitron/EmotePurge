import { describe, expect, it } from 'vitest';

import { chunkIntoRows, computeGridColumns } from './grid-columns';

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
