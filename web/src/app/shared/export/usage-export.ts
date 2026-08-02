import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageTrend } from '../emotes/emote-context';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';

export interface UsageExportInput {
  channelName: string;
  /** ISO dates (`yyyy-MM-dd`) of the selected range, both inclusive. */
  from: string;
  to: string;
  /** The visible (filtered + sorted) list — exporting rows the user is not looking at surprises. */
  rows: readonly EmoteUsageTotal[];
  /** Whether `rows` is a filtered subset of the channel's emotes. */
  filtered: boolean;
  /** The page owns the trend derivation (it knows `trackedSince`) — injected, not re-derived. */
  trendFor: (row: EmoteUsageTotal) => UsageTrend;
}

export interface UsageExportRow {
  emoteName: string;
  sevenTvEmoteId: string;
  totalUseCount: number;
  previousWindowUseCount: number;
  lastUsedDate: string | null;
  firstSeenAt: string | null;
  /** `unknown` = deliberately not stated (thin data), never a missing value. */
  trend: UsageTrend;
}

export interface UsageExportMeta {
  from: string;
  to: string;
  rowCount: number;
  filtered: boolean;
}

export function usageExportFilename(input: UsageExportInput, ext: 'csv' | 'json'): string {
  return `emotepurge_${sanitizeFilenamePart(input.channelName)}_usage_${input.from}_${input.to}.${ext}`;
}

export function usageCsv(input: UsageExportInput): string {
  const columns: CsvColumn<EmoteUsageTotal>[] = [
    { header: 'emote_name', value: (row) => row.emoteName },
    { header: 'seven_tv_emote_id', value: (row) => row.sevenTvEmoteId },
    { header: 'total_use_count', value: (row) => row.totalUseCount },
    { header: 'previous_window_use_count', value: (row) => row.previousWindowUseCount },
    { header: 'last_used_date', value: (row) => row.lastUsedDate },
    { header: 'first_seen_at', value: (row) => row.firstSeenAt },
    { header: 'trend', value: (row) => input.trendFor(row) },
  ];
  return toCsv(input.rows, columns);
}

export function usageJson(input: UsageExportInput): string {
  const envelope: ExportEnvelope<UsageExportRow, UsageExportMeta> = buildEnvelope({
    kind: 'usage',
    channelName: input.channelName,
    // Every usage figure is visible to whoever can open this page — nothing is withheld here.
    withheld: [],
    meta: {
      from: input.from,
      to: input.to,
      rowCount: input.rows.length,
      filtered: input.filtered,
    },
    rows: input.rows.map((row) => ({
      emoteName: row.emoteName,
      sevenTvEmoteId: row.sevenTvEmoteId,
      totalUseCount: row.totalUseCount,
      previousWindowUseCount: row.previousWindowUseCount,
      lastUsedDate: row.lastUsedDate,
      firstSeenAt: row.firstSeenAt,
      trend: input.trendFor(row),
    })),
  });
  return JSON.stringify(envelope, null, 2);
}
