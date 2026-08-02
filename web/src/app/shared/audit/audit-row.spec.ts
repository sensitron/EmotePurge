import { describe, expect, it } from 'vitest';

import { toAuditRows } from './audit-row';
import { AuditLogEntry } from '../../core/audit/audit.model';

function entry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: 1,
    occurredAtUtc: '2026-07-31T12:00:00Z',
    actorLogin: 'sensitron',
    action: 'channel.join',
    channelName: 'handofblood',
    targetType: null,
    targetId: null,
    detail: null,
    ...overrides,
  };
}

describe('toAuditRows', () => {
  it('resolves a known action to its translation key', () => {
    const [row] = toAuditRows([entry({ action: 'voteSession.delete' })], 'de-DE');

    expect(row.actionKey).toBe('audit.actions.voteSessionDelete');
    expect(row.action).toBe('voteSession.delete');
  });

  it('leaves an unknown action without a key but keeps it verbatim', () => {
    // An entry written by a newer backend: showing the raw string beats hiding the row.
    const [row] = toAuditRows([entry({ action: 'channel.somethingNew' })], 'de-DE');

    expect(row.actionKey).toBeNull();
    expect(row.action).toBe('channel.somethingNew');
  });

  it('renders a counting detail with its count parameter', () => {
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'emoteCount', count: 12, text: null } })],
      'de-DE',
    );

    expect(row.detail).toEqual({ key: 'audit.details.emoteCount', params: { count: 12 } });
  });

  it('renders a naming detail with its title parameter', () => {
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'title', count: null, text: 'Sommer-Purge' } })],
      'de-DE',
    );

    expect(row.detail).toEqual({
      key: 'audit.details.title',
      params: { title: 'Sommer-Purge' },
    });
  });

  it('drops a detail kind this build has no label for', () => {
    // Not a safety decision — the server already whitelisted the payload. This build is simply
    // older than the backend, and the row keeps its action and actor.
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'somethingNew', count: 3, text: null } })],
      'de-DE',
    );

    expect(row.detail).toBeNull();
  });

  it('formats the timestamp in the given locale', () => {
    const rows = toAuditRows([entry({ occurredAtUtc: '2026-07-31T12:00:00Z' })], 'en-US');

    // Only the shape is asserted: the exact string depends on the runtime's timezone.
    expect(rows[0].timestamp).toMatch(/\d/);
    expect(rows[0].occurredAtUtc).toBe('2026-07-31T12:00:00Z');
  });
});
