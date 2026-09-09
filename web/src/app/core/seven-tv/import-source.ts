/**
 * One row copied by the import flow (#72, K3): the minimal identity a 7TV ADD mutation needs.
 * Shared shape across the origins — a tracked channel's active set (via the emote grid), a
 * downloaded file (emote-list export or usage export, parsed by `shared/export/import-source-parser`)
 * and a foreign channel's set read straight from 7TV (`shared/seven-tv/foreign-channel-import-dialog`).
 */
export interface ImportRow {
  sevenTvEmoteId: string;
  name: string;
}

/**
 * Where the rows came from. Kept next to the run (not just used to build it once) because the
 * import service's summary and its resync report both need to say "from channel X" / "from file Y"
 * after the run has started.
 *
 * `kind` is also the wire vocabulary of `POST .../emotes/sync-imported` (`SyncImportedBody`), and
 * the server keeps every value forever in a write-once audit row. Adding a member here therefore
 * means three server-side places, not one: the endpoint's accepted vocabulary, its kind-versus-name
 * agreement, and `AuditLogQueryService`'s provenance branch — a word the last one does not know
 * costs every row written with it its origin, silently (spec F5).
 *
 * `'channel'` and `'seventv-channel'` are deliberately two words rather than one: the first is a
 * channel EmotePurge tracks and reads out of its own database, the second is any Twitch login, read
 * live from 7TV by a user with no role in it. They behave the same from here on — which is why
 * `!== 'file'` and `=== 'channel'` are no longer interchangeable anywhere in this codebase (F6). Use
 * {@link importOriginSourceChannelName} rather than either.
 */
export type ImportOrigin =
  | { kind: 'channel'; channelName: string }
  | { kind: 'seventv-channel'; channelName: string }
  | {
      kind: 'file';
      fileName: string;
      exportedAt: string | null;
      channelName: string | null;
      envelopeKind: 'emote-list' | 'usage';
    };

/**
 * The source channel name that belongs on the wire for an origin, or `null` when the origin has
 * none. The one place the union is taken apart for that question, and deliberately exhaustive: a
 * fourth member makes the `default` arm below a compile error instead of quietly reaching the server
 * as `null`.
 *
 * That failure is the reason this function exists. `sync-imported` rejects any non-file kind without
 * a source name with a 400 — and it runs *after* the 7TV mutations, so a wrong `null` here means the
 * emotes are already copied, the report fails, and the provenance is gone for good (spec F6).
 *
 * A file origin sends `null` even when the file itself names a channel: the server rejects `file`
 * *with* a name as `invalid_source_kind` (R3, K2 contract).
 */
export function importOriginSourceChannelName(origin: ImportOrigin): string | null {
  switch (origin.kind) {
    case 'channel':
    case 'seventv-channel':
      return origin.channelName;
    case 'file':
      return null;
    default:
      return assertUnreachableOrigin(origin);
  }
}

/** Reached only when a new {@link ImportOrigin} member skipped a `switch` above — the parameter type
 *  is what makes that a build error rather than a runtime surprise. */
function assertUnreachableOrigin(origin: never): never {
  throw new Error(`Unbekannte Import-Herkunft: ${JSON.stringify(origin)}`);
}

/**
 * The rows an import run is offered, already deduplicated by `sevenTvEmoteId` (see
 * `dedupeImportRows`). `duplicatesCollapsed` counts *valid* rows that were folded away by that
 * dedup; `discardedRows` counts rows the source rejected as invalid before dedup even ran — the
 * two never overlap. `discardedRows` is always `0` for both channel origins: `'channel'` rows come
 * out of the grid, which only ever holds rows that passed `EmoteListItem` validation server-side,
 * and `'seventv-channel'` rows come out of our own `/api/seventv/...` endpoint, which parses 7TV's
 * answer into `ForeignEmoteRow` before it ever reaches the browser. Only a parsed file can discard
 * rows, because only a file is written by something other than us.
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
