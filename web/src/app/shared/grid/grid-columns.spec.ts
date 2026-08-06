import { describe, expect, it } from 'vitest';

import { chunkIntoRows } from './grid-columns';

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
