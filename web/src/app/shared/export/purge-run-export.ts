import { RunItemStatus, RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, ExportKind, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';
import { readEnvelope } from './read-envelope';

/**
 * The purge-run protocol (A6): the paper trail of a mass delete, downloadable as JSON/CSV and
 * re-importable as a restore list. The third `kind` of the shared export envelope — and the reason
 * that envelope carries `source`/`kind`/`formatVersion`: an imported file is validated against
 * them instead of trusted. Deliberately contains no token of any kind, only emote ids and names.
 */

export interface PurgeRunRow {
  emoteId: string;
  sevenTvEmoteId: string;
  name: string;
  status: RunItemStatus;
  errorMessage: string | null;
}

export interface PurgeRunMeta {
  emoteSetId: string;
  /** ISO timestamps of the run itself. */
  startedAt: string;
  finishedAt: string;
  counts: { requested: number; succeeded: number; failed: number; cancelled: number };
}

export type PurgeRunProtocol = ExportEnvelope<PurgeRunRow, PurgeRunMeta>;

export function buildPurgeRunProtocol(input: {
  channelName: string;
  emoteSetId: string;
  startedAt: number;
  finishedAt: number;
  // Narrowed rather than plain RunQueueItem: since #70 a queue row's emoteId is optional (an
  // import run has no internal guid for the target channel), but PurgeRunRow.emoteId is required.
  // Demanding it here makes an unprotocollable run a compile error at the call site instead of a
  // silently short download — and keeps `counts` below, which is derived from every item, in step
  // with `rows`.
  items: readonly (RunQueueItem & { emoteId: string })[];
}): PurgeRunProtocol {
  const statuses = input.items.map((item) => item.status);
  return buildEnvelope({
    kind: 'purge-run',
    channelName: input.channelName,
    withheld: [],
    meta: {
      emoteSetId: input.emoteSetId,
      startedAt: new Date(input.startedAt).toISOString(),
      finishedAt: new Date(input.finishedAt).toISOString(),
      counts: {
        requested: statuses.length,
        succeeded: statuses.filter((status) => status === 'done').length,
        failed: statuses.filter((status) => status === 'failed').length,
        cancelled: statuses.filter((status) => status === 'cancelled').length,
      },
    },
    rows: input.items.map((item) => ({
      emoteId: item.emoteId,
      sevenTvEmoteId: item.sevenTvEmoteId,
      name: item.name,
      status: item.status,
      errorMessage: item.errorMessage ?? null,
    })),
  });
}

export function purgeRunJson(protocol: PurgeRunProtocol): string {
  return JSON.stringify(protocol, null, 2);
}

export function purgeRunCsv(protocol: PurgeRunProtocol): string {
  const columns: CsvColumn<PurgeRunRow>[] = [
    { header: 'name', value: (row) => row.name },
    { header: 'seven_tv_emote_id', value: (row) => row.sevenTvEmoteId },
    { header: 'status', value: (row) => row.status },
    { header: 'error_message', value: (row) => row.errorMessage },
  ];
  return toCsv(protocol.rows, columns);
}

export function purgeRunFilename(
  channelName: string,
  finishedAt: string,
  ext: 'csv' | 'json',
): string {
  // yyyy-mm-dd-HHmm of the run end — a channel can run several purges a day.
  const stamp = finishedAt.slice(0, 16).replace('T', '-').replace(':', '');
  return `emotepurge_${sanitizeFilenamePart(channelName)}_purge_${stamp}.${ext}`;
}

/**
 * The other envelope kinds this parser can name explicitly, mapped to an error that says what the
 * file actually is. A usage export used to need its own entry here, but since #72 it is itself an
 * importable source (see `import-source-parser`) — the file dispatch tries that parser first, so a
 * usage export reaching *this* function at all would be unexpected, and the generic `wrongKind` is
 * an honest enough answer for it. Voting exports have no import path anywhere, hence the one entry
 * left.
 */
const FOREIGN_KIND_ERROR_KEYS: Partial<Record<ExportKind, string>> = {
  voting: 'restore.import.errors.votingExport',
};

export type ProtocolParseResult =
  | { ok: true; rows: PurgeRunRow[]; meta: PurgeRunMeta; channelName: string }
  /** `errorKey` is a Transloco key (restore.import.errors.*), never finished prose. */
  | { ok: false; errorKey: string };

/**
 * Validates an uploaded protocol against the *current* channel and active set — the file can be
 * days old and the channel can have switched sets since; restoring against the wrong set must be
 * a refusal, not a surprise. Returns only rows with `status: 'done'`: a failed delete means the
 * emote never left the set, and re-adding it would at best be a no-op, at worst an alias
 * collision.
 */
export function parsePurgeRunProtocol(
  text: string,
  expected: { channelName: string; emoteSetId: string },
): ProtocolParseResult {
  const read = readEnvelope(text);
  if (!read.ok) {
    return read;
  }

  // `read.envelope` is `ExportEnvelope<unknown>` — its `meta` is an untyped `Record<string,
  // unknown>`, which structurally shares nothing with `PurgeRunMeta`'s required fields, so TS
  // refuses the direct cast. Going through `unknown` says out loud what every check below already
  // does: treat this as unverified JSON from a file and validate each field by hand.
  const envelope = read.envelope as unknown as Partial<PurgeRunProtocol>;
  if (envelope.kind !== 'purge-run') {
    // Ours, but the wrong export — `kind` is untrusted input, so an unknown value falls back.
    const foreign = envelope.kind ? FOREIGN_KIND_ERROR_KEYS[envelope.kind] : undefined;
    return { ok: false, errorKey: foreign ?? 'restore.import.errors.wrongKind' };
  }
  if (envelope.formatVersion !== 1) {
    return { ok: false, errorKey: 'restore.import.errors.wrongVersion' };
  }
  if (envelope.channelName !== expected.channelName) {
    return { ok: false, errorKey: 'restore.import.errors.wrongChannel' };
  }
  const meta = envelope.meta;
  if (!meta || typeof meta.emoteSetId !== 'string') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  if (meta.emoteSetId !== expected.emoteSetId) {
    return { ok: false, errorKey: 'restore.import.errors.wrongSet' };
  }
  if (!Array.isArray(envelope.rows)) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }

  const restorable = envelope.rows.filter(
    (row): row is PurgeRunRow =>
      !!row &&
      typeof row.emoteId === 'string' &&
      typeof row.sevenTvEmoteId === 'string' &&
      row.sevenTvEmoteId.length > 0 &&
      typeof row.name === 'string' &&
      row.status === 'done',
  );
  if (restorable.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.noRestorableRows' };
  }

  return { ok: true, rows: restorable, meta, channelName: envelope.channelName };
}
