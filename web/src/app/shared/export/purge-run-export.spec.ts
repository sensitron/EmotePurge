import { describe, expect, it } from 'vitest';

import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import {
  buildPurgeRunProtocol,
  parsePurgeRunProtocol,
  purgeRunCsv,
  purgeRunFilename,
  purgeRunJson,
} from './purge-run-export';

const ITEMS: RunQueueItem[] = [
  { emoteId: 'i1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
  { emoteId: 'i2', sevenTvEmoteId: '7tv-2', name: 'KEKW', status: 'failed', errorMessage: 'boom' },
  { emoteId: 'i3', sevenTvEmoteId: '7tv-3', name: 'catJAM', status: 'cancelled' },
];

function protocol() {
  return buildPurgeRunProtocol({
    channelName: 'sensitron',
    emoteSetId: 'set-1',
    startedAt: Date.parse('2026-08-02T10:00:00Z'),
    finishedAt: Date.parse('2026-08-02T10:05:00Z'),
    items: ITEMS,
  });
}

describe('buildPurgeRunProtocol', () => {
  it('wraps the run in the shared envelope with counts and set id', () => {
    const proto = protocol();
    expect(proto.source).toBe('emotepurge');
    expect(proto.kind).toBe('purge-run');
    expect(proto.channelName).toBe('sensitron');
    expect(proto.meta.emoteSetId).toBe('set-1');
    expect(proto.meta.counts).toEqual({ requested: 3, succeeded: 1, failed: 1, cancelled: 1 });
    expect(proto.rows).toHaveLength(3);
    expect(proto.rows[1].errorMessage).toBe('boom');
  });

  it('contains no token anywhere', () => {
    expect(purgeRunJson(protocol())).not.toMatch(/token|authorization|bearer/i);
  });
});

describe('purgeRunCsv', () => {
  it('emits name, id, status and error columns', () => {
    const lines = purgeRunCsv(protocol()).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[0]).toBe('name,seven_tv_emote_id,status,error_message');
    expect(lines[1]).toBe('PogU,7tv-1,done,');
    expect(lines[2]).toBe('KEKW,7tv-2,failed,boom');
  });
});

describe('purgeRunFilename', () => {
  it('stamps channel and run end down to the minute', () => {
    expect(purgeRunFilename('Sensitron', '2026-08-02T10:05:12Z', 'json')).toBe(
      'emotepurge_sensitron_purge_2026-08-02-1005.json',
    );
  });
});

describe('parsePurgeRunProtocol', () => {
  const EXPECTED = { channelName: 'sensitron', emoteSetId: 'set-1' };

  it('accepts a matching protocol and returns only the done rows', () => {
    const result = parsePurgeRunProtocol(purgeRunJson(protocol()), EXPECTED);
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.rows.map((row) => row.emoteId)).toEqual(['i1']);
      expect(result.meta.emoteSetId).toBe('set-1');
    }
  });

  it('rejects non-JSON', () => {
    expect(parsePurgeRunProtocol('nope{', EXPECTED)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.notJson',
    });
  });

  it('rejects a different export kind', () => {
    const voting = JSON.stringify({ source: 'emotepurge', kind: 'voting', formatVersion: 1 });
    expect(parsePurgeRunProtocol(voting, EXPECTED)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('rejects an unknown format version', () => {
    const future = purgeRunJson(protocol()).replace('"formatVersion": 1', '"formatVersion": 99');
    expect(parsePurgeRunProtocol(future, EXPECTED)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongVersion',
    });
  });

  it('rejects a protocol from another channel', () => {
    const result = parsePurgeRunProtocol(purgeRunJson(protocol()), {
      channelName: 'handofblood',
      emoteSetId: 'set-1',
    });
    expect(result).toEqual({ ok: false, errorKey: 'restore.import.errors.wrongChannel' });
  });

  it('rejects a protocol against a different active set', () => {
    const result = parsePurgeRunProtocol(purgeRunJson(protocol()), {
      channelName: 'sensitron',
      emoteSetId: 'other-set',
    });
    expect(result).toEqual({ ok: false, errorKey: 'restore.import.errors.wrongSet' });
  });

  it('rejects a protocol without rows array', () => {
    const broken = JSON.stringify({
      source: 'emotepurge',
      kind: 'purge-run',
      formatVersion: 1,
      channelName: 'sensitron',
      meta: { emoteSetId: 'set-1' },
    });
    expect(parsePurgeRunProtocol(broken, EXPECTED)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('drops malformed rows and rejects when nothing restorable remains', () => {
    const proto = protocol();
    proto.rows = [
      { ...proto.rows[1] }, // failed — never left the set
      { ...proto.rows[0], sevenTvEmoteId: '' }, // malformed
    ];
    expect(parsePurgeRunProtocol(purgeRunJson(proto), EXPECTED)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.noRestorableRows',
    });
  });
});
