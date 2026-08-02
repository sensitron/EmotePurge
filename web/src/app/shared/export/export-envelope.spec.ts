import { describe, expect, it } from 'vitest';

import { EXPORT_FORMAT_VERSION, buildEnvelope } from './export-envelope';

describe('buildEnvelope', () => {
  it('stamps source, formatVersion and an ISO exportedAt', () => {
    const envelope = buildEnvelope({
      kind: 'usage',
      channelName: 'sensitron',
      withheld: [],
      meta: { rowCount: 0 },
      rows: [],
    });

    expect(envelope.source).toBe('emotepurge');
    expect(envelope.formatVersion).toBe(EXPORT_FORMAT_VERSION);
    expect(new Date(envelope.exportedAt).getTime()).not.toBeNaN();
  });

  it('passes withheld, meta and rows through untouched', () => {
    const rows = [{ emoteName: 'PogU' }];
    const envelope = buildEnvelope({
      kind: 'voting',
      channelName: 'sensitron',
      withheld: ['keepVotes'],
      meta: { sessionId: 7 },
      rows,
    });

    expect(envelope.kind).toBe('voting');
    expect(envelope.withheld).toEqual(['keepVotes']);
    expect(envelope.meta).toEqual({ sessionId: 7 });
    expect(envelope.rows).toBe(rows);
  });
});
