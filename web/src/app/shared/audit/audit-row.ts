import { ACTION_KEYS, DETAIL_KEYS } from './audit-actions';
import { AuditLogDetail, AuditLogEntry } from '../../core/audit/audit.model';

/** A detail reduced to what the template hands to Transloco. */
export interface RenderedDetail {
  key: string;
  params: Record<string, string | number>;
}

/** A row as the template consumes it — every derivation done once, in TypeScript. */
export interface AuditRow {
  id: number;
  occurredAtUtc: string;
  timestamp: string;
  actorLogin: string;
  /** Translation key for the action, or null when this build does not know the action. */
  actionKey: string | null;
  /** The raw action string, shown verbatim when `actionKey` is null. */
  action: string;
  channelName: string | null;
  detail: RenderedDetail | null;
}

/**
 * Projects wire entries into rows for a given locale.
 *
 * The locale is a parameter rather than read from a service, so a language switch re-formats the
 * timestamps: `LOCALE_ID` is fixed at bootstrap and cannot follow one. Seconds are shown because
 * several audited actions can legitimately land in the same minute.
 */
export function toAuditRows(entries: readonly AuditLogEntry[], locale: string): AuditRow[] {
  return entries.map((entry) => ({
    id: entry.id,
    occurredAtUtc: entry.occurredAtUtc,
    timestamp: new Date(entry.occurredAtUtc).toLocaleString(locale, {
      dateStyle: 'short',
      timeStyle: 'medium',
    }),
    actorLogin: entry.actorLogin,
    actionKey: ACTION_KEYS[entry.action] ?? null,
    action: entry.action,
    channelName: entry.channelName,
    detail: renderDetail(entry.detail),
  }));
}

/**
 * Builds the interpolation params conditionally, one per field that is actually set, rather than
 * picking either-or: `importedFromChannel` is the one kind that carries both a count and a title
 * at once (R1 in the #71 import plan), and the three older kinds each set exactly one of the two
 * fields anyway, so this stays a no-op change for them.
 */
function renderDetail(detail: AuditLogDetail | null): RenderedDetail | null {
  const key = detail && DETAIL_KEYS[detail.kind];
  if (!detail || !key) {
    return null;
  }

  const params: Record<string, string | number> = {};
  if (detail.count !== null) {
    params['count'] = detail.count;
  }
  if (detail.text !== null) {
    params['title'] = detail.text;
  }

  return { key, params };
}
