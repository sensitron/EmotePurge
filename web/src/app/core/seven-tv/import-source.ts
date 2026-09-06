/**
 * One row copied by the import flow (#72, K3): the minimal identity a 7TV ADD mutation needs.
 * Shared shape between the two origins — a channel's active set (via the emote grid) and a
 * downloaded file (emote-list export or usage export, parsed by `shared/export/import-source-parser`).
 */
export interface ImportRow {
  sevenTvEmoteId: string;
  name: string;
}

/**
 * Where the rows came from. Kept next to the run (not just used to build it once) because the
 * import service's summary and its resync report both need to say "from channel X" / "from file Y"
 * after the run has started.
 */
export type ImportOrigin =
  | { kind: 'channel'; channelName: string }
  | {
      kind: 'file';
      fileName: string;
      exportedAt: string | null;
      channelName: string | null;
      envelopeKind: 'emote-list' | 'usage';
    };

/**
 * The rows an import run is offered, already deduplicated by `sevenTvEmoteId` (see
 * `dedupeImportRows`). `duplicatesCollapsed` counts *valid* rows that were folded away by that
 * dedup; `discardedRows` counts rows the source rejected as invalid before dedup even ran — the
 * two never overlap. For `origin.kind === 'channel'` `discardedRows` is always `0`: the channel
 * grid only ever supplies rows that already passed `EmoteListItem` validation server-side.
 */
export interface ImportSource {
  origin: ImportOrigin;
  rows: ImportRow[];
  duplicatesCollapsed: number;
  discardedRows: number;
}

/**
 * The one deduplication both origins go through — first occurrence of a `sevenTvEmoteId` wins,
 * order of the surviving rows follows their first occurrence. Ordinal comparison on the id (plain
 * string equality): these are 7TV object ids, not display text, so there is nothing to localize.
 */
export function dedupeImportRows(rows: readonly ImportRow[]): {
  rows: ImportRow[];
  duplicatesCollapsed: number;
} {
  const seen = new Set<string>();
  const deduped: ImportRow[] = [];

  for (const row of rows) {
    if (seen.has(row.sevenTvEmoteId)) {
      continue;
    }
    seen.add(row.sevenTvEmoteId);
    deduped.push(row);
  }

  return { rows: deduped, duplicatesCollapsed: rows.length - deduped.length };
}
