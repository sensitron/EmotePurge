/** Splits a flat, already-ordered list into fixed-size rows for CdkVirtualScrollViewport, which
 *  virtualizes one dimension (rows) — each row then lays out `columns` cells via CSS grid.
 *
 *  Its former neighbour `computeGridColumns` is gone (2026-08-06): it mapped the *viewport* width to
 *  a column count through breakpoints, while the shell caps content at 1024 px — so a wide monitor
 *  got eight cards stretched across 992 px. Both grids now measure their own container instead, via
 *  `atlasColumns`. */
export function chunkIntoRows<T>(items: readonly T[], columns: number): T[][] {
  if (columns <= 0) {
    return [items.slice()];
  }

  const rows: T[][] = [];
  for (let i = 0; i < items.length; i += columns) {
    rows.push(items.slice(i, i + columns));
  }
  return rows;
}
