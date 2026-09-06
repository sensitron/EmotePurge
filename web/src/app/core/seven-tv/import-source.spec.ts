import { describe, expect, it } from 'vitest';

import { ImportRow, dedupeImportRows } from './import-source';

describe('dedupeImportRows', () => {
  it('collapses a duplicate sevenTvEmoteId and keeps the first occurrence', () => {
    const rows: ImportRow[] = [
      { sevenTvEmoteId: 'a1', name: 'PogU' },
      { sevenTvEmoteId: 'a2', name: 'Kappa' },
      { sevenTvEmoteId: 'a1', name: 'PogU-again' },
    ];

    const result = dedupeImportRows(rows);

    expect(result.duplicatesCollapsed).toBe(1);
    expect(result.rows).toEqual([
      { sevenTvEmoteId: 'a1', name: 'PogU' },
      { sevenTvEmoteId: 'a2', name: 'Kappa' },
    ]);
  });

  it('reports zero collapsed and preserves order when there are no duplicates', () => {
    const rows: ImportRow[] = [
      { sevenTvEmoteId: 'a1', name: 'PogU' },
      { sevenTvEmoteId: 'a2', name: 'Kappa' },
      { sevenTvEmoteId: 'a3', name: 'monkaS' },
    ];

    const result = dedupeImportRows(rows);

    expect(result.duplicatesCollapsed).toBe(0);
    expect(result.rows).toEqual(rows);
  });

  it('returns an empty result for an empty input', () => {
    const result = dedupeImportRows([]);

    expect(result.rows).toEqual([]);
    expect(result.duplicatesCollapsed).toBe(0);
  });
});
