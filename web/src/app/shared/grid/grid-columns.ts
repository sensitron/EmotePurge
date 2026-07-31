/**
 * Maps viewport width to a column count for the emote grids on the Usage-Stats and Voting-Result
 * pages — the two places that list up to ~1000 emotes and needed an actual grid instead of a
 * single-column list (scrolling through 1000 one-per-row rows was the reported problem).
 */
export function computeGridColumns(width: number): number {
  if (width >= 1536) return 8;
  if (width >= 1280) return 7;
  if (width >= 1024) return 5;
  if (width >= 768) return 4;
  if (width >= 640) return 3;
  return 2;
}

/**
 * Below this width the vote cards switch to their touch layout (44px vote buttons), which makes
 * the virtualized rows taller. Same 640px boundary as the 2→3 column step above and Tailwind's
 * `sm:`, so the CSS breakpoint and the JS row height can never disagree.
 */
export const COMPACT_VIEWPORT_MAX_WIDTH = 640;

export function isCompactViewport(width: number): boolean {
  return width < COMPACT_VIEWPORT_MAX_WIDTH;
}

/** Splits a flat, already-ordered list into fixed-size rows for CdkVirtualScrollViewport, which
 *  virtualizes one dimension (rows) — each row then lays out `columns` cards via CSS grid. */
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
