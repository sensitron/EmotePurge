import { describe, expect, it } from 'vitest';

import { readEnvelope } from './read-envelope';

describe('readEnvelope', () => {
  it('rejects non-JSON as notJson', () => {
    expect(readEnvelope('nope{')).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.notJson',
    });
  });

  it('tells the CSV version of an export apart from a corrupt file', () => {
    const csv = '﻿name,seven_tv_emote_id\r\nPogU,7tv-1\r\n';
    expect(readEnvelope(csv)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.csvInsteadOfJson',
    });
  });

  it('rejects a foreign source as wrongKind', () => {
    expect(readEnvelope(JSON.stringify({ source: 'someone-else', kind: 'usage' }))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
    expect(readEnvelope(JSON.stringify({ hello: 'world' }))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('rejects a non-string kind as wrongKind', () => {
    expect(readEnvelope(JSON.stringify({ source: 'emotepurge', kind: 42 }))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
    expect(readEnvelope(JSON.stringify({ source: 'emotepurge' }))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('passes a valid envelope through with its kind intact', () => {
    const result = readEnvelope(
      JSON.stringify({
        source: 'emotepurge',
        kind: 'emote-list',
        formatVersion: 1,
        exportedAt: '2026-09-05T10:00:00Z',
        channelName: 'sensitron',
        withheld: [],
        meta: { rowCount: 0 },
        rows: [],
      }),
    );

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.envelope.kind).toBe('emote-list');
      expect(result.envelope.channelName).toBe('sensitron');
    }
  });
});
