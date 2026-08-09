import { describe, expect, it } from 'vitest';

import { UsageBandKey } from '../emotes/usage-bands';
import { AtlasRow, atlasColumns, atlasRowOfIndex, moveInAtlas, packAtlasRows } from './atlas-grid';

/** Two bands, five then three cells — enough to exercise a short row and a band boundary. */
function twoBands(columns = 3) {
  const bands: { key: UsageBandKey; items: string[]; share: number }[] = [
    { key: 'heavy', items: ['a', 'b', 'c', 'd', 'e'], share: 0.8 },
    { key: 'dead', items: ['f', 'g', 'h'], share: 0 },
  ];
  return packAtlasRows(bands, columns);
}

describe('atlasColumns', () => {
  it('fills the width it is given', () => {
    // 992 px of content: (992 + 4) / 68 = 14 whole cells.
    expect(atlasColumns(992)).toBe(14);
  });

  it('fits exactly one cell into exactly one cell of width', () => {
    expect(atlasColumns(64)).toBe(1);
  });

  it('takes the second cell only once its gutter fits too', () => {
    expect(atlasColumns(131)).toBe(1);
    expect(atlasColumns(132)).toBe(2);
  });

  it('packs a wider cell, which is what the ballot uses on a narrow screen', () => {
    // 96 px cell + 4 px gutter: (358 + 4) / 100 = 3 across a 390 px phone's content width.
    expect(atlasColumns(358, 96)).toBe(3);
    expect(atlasColumns(992, 96)).toBe(9);
  });

  it('never returns zero columns before the first measurement', () => {
    // A zero width is what the ResizeObserver reports for one frame on a hidden container;
    // returning 0 there would divide the row packing by zero.
    expect(atlasColumns(0)).toBe(1);
    expect(atlasColumns(-10)).toBe(1);
    expect(atlasColumns(Number.NaN)).toBe(1);
  });
});

describe('packAtlasRows', () => {
  it('gives every band its own header row', () => {
    const { rows } = twoBands();

    expect(rows.map((row) => row.kind)).toEqual(['band', 'cells', 'cells', 'band', 'cells']);
  });

  it('never continues a band on a row that already holds the previous one', () => {
    const { rows } = twoBands();
    const lastHeavyRow = rows[2] as Extract<AtlasRow<string>, { kind: 'cells' }>;
    const firstDeadRow = rows[4] as Extract<AtlasRow<string>, { kind: 'cells' }>;

    expect(lastHeavyRow.items).toEqual(['d', 'e']);
    expect(firstDeadRow.items).toEqual(['f', 'g', 'h']);
  });

  it('numbers the cells continuously across band boundaries', () => {
    const { rows, flat } = twoBands();
    const cellRows = rows.filter(
      (row): row is Extract<AtlasRow<string>, { kind: 'cells' }> => row.kind === 'cells',
    );

    expect(cellRows.map((row) => row.startIndex)).toEqual([0, 3, 5]);
    expect(flat).toEqual(['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h']);
  });

  it('drops an empty band instead of leaving a header with nothing under it', () => {
    const { rows } = packAtlasRows(
      [
        { key: 'heavy', items: ['a'], share: 0.9 },
        { key: 'regular', items: [], share: 0 },
        { key: 'dead', items: ['b'], share: 0 },
      ],
      4,
    );

    expect(rows.filter((row) => row.kind === 'band')).toHaveLength(2);
  });

  it('falls back to a single column rather than dividing by zero', () => {
    const { rows } = packAtlasRows([{ key: 'heavy', items: ['a', 'b'], share: 1 }], 0);

    expect(rows).toHaveLength(3);
  });

  it('carries each band its share into the header row', () => {
    const { rows } = twoBands();
    const headers = rows.filter(
      (row): row is Extract<AtlasRow<string>, { kind: 'band' }> => row.kind === 'band',
    );

    expect(headers.map((row) => row.share)).toEqual([0.8, 0]);
    expect(headers.map((row) => row.count)).toEqual([5, 3]);
  });
});

describe('moveInAtlas', () => {
  const { rows } = twoBands();

  it('steps sideways through the flat order', () => {
    expect(moveInAtlas(rows, 1, 'ArrowRight')).toBe(2);
    expect(moveInAtlas(rows, 1, 'ArrowLeft')).toBe(0);
  });

  it('stops at both ends instead of wrapping', () => {
    expect(moveInAtlas(rows, 0, 'ArrowLeft')).toBeNull();
    expect(moveInAtlas(rows, 7, 'ArrowRight')).toBeNull();
    expect(moveInAtlas(rows, 0, 'ArrowUp')).toBeNull();
    expect(moveInAtlas(rows, 7, 'ArrowDown')).toBeNull();
  });

  it('keeps the column when moving down a full row', () => {
    // Row 1 is [a b c] at 0..2, row 2 is [d e] at 3..4 — column 1 stays column 1.
    expect(moveInAtlas(rows, 1, 'ArrowDown')).toBe(4);
  });

  it('clamps onto the last cell of a shorter row', () => {
    // Column 2 of [a b c] has no counterpart in [d e]; landing somewhere beats refusing to move.
    expect(moveInAtlas(rows, 2, 'ArrowDown')).toBe(4);
  });

  it('crosses a band boundary without counting the header as a row', () => {
    // Adding `columns` to the index would land on 7 here, because the band header sits in between.
    expect(moveInAtlas(rows, 4, 'ArrowDown')).toBe(6);
    expect(moveInAtlas(rows, 6, 'ArrowUp')).toBe(4);
  });

  it('jumps to the ends of the whole atlas', () => {
    expect(moveInAtlas(rows, 4, 'Home')).toBe(0);
    expect(moveInAtlas(rows, 4, 'End')).toBe(7);
  });

  it('leaves every other key alone', () => {
    // Anything this returns null for keeps its default behaviour — Tab must still leave the grid.
    expect(moveInAtlas(rows, 1, 'Tab')).toBeNull();
    expect(moveInAtlas(rows, 1, 'a')).toBeNull();
    expect(moveInAtlas(rows, 1, 'Enter')).toBeNull();
  });

  it('refuses to move from an index that is not in the atlas', () => {
    expect(moveInAtlas(rows, 99, 'ArrowRight')).toBeNull();
    expect(moveInAtlas([], 0, 'ArrowRight')).toBeNull();
  });
});

describe('atlasRowOfIndex', () => {
  it('finds the virtualized row a cell lives in', () => {
    const { rows } = twoBands();

    expect(atlasRowOfIndex(rows, 0)).toBe(1);
    expect(atlasRowOfIndex(rows, 4)).toBe(2);
    expect(atlasRowOfIndex(rows, 5)).toBe(4);
  });

  it('reports a miss rather than a header row', () => {
    expect(atlasRowOfIndex(twoBands().rows, 99)).toBe(-1);
  });
});
