import { describe, expect, it } from 'vitest';

import {
  SET_STATUS_FLUSH_PROBE_LIMIT,
  SetStatusFlushProbeGate,
} from './set-status-flush-probe-gate';

describe('SetStatusFlushProbeGate', () => {
  it('allows exactly the first SET_STATUS_FLUSH_PROBE_LIMIT bursts through, then none', () => {
    const gate = new SetStatusFlushProbeGate();
    const incomplete = { botsExcludedSince: null, sharedChatSeparatedSince: null };

    const decisions = Array.from({ length: SET_STATUS_FLUSH_PROBE_LIMIT + 2 }, () =>
      gate.shouldRefreshOn(incomplete),
    );

    expect(decisions).toEqual([
      ...Array<boolean>(SET_STATUS_FLUSH_PROBE_LIMIT).fill(true),
      false,
      false,
    ]);
  });

  it('short-circuits to false on the very first burst once both fields are already filled in', () => {
    const gate = new SetStatusFlushProbeGate();

    const allowed = gate.shouldRefreshOn({
      botsExcludedSince: '2026-08-01T00:00:00Z',
      sharedChatSeparatedSince: '2026-08-02T00:00:00Z',
    });

    expect(allowed).toBe(false);
  });

  it('does not spend a probe on the completed-fields shortcut', () => {
    // Regression: the shortcut must return before decrementing, otherwise a channel that already
    // has both dates on mount would still burn through the budget for nothing, and a later probe
    // that *would* have taught it something (e.g. after a status reset) would be starved.
    const gate = new SetStatusFlushProbeGate();
    const complete = {
      botsExcludedSince: '2026-08-01T00:00:00Z',
      sharedChatSeparatedSince: '2026-08-02T00:00:00Z',
    };

    gate.shouldRefreshOn(complete);
    gate.shouldRefreshOn(complete);

    const stillIncomplete = {
      botsExcludedSince: '2026-08-01T00:00:00Z',
      sharedChatSeparatedSince: null,
    };
    expect(gate.shouldRefreshOn(stillIncomplete)).toBe(true);
  });

  it('treats a single missing field as still incomplete (AND, not OR)', () => {
    const gate = new SetStatusFlushProbeGate();

    expect(
      gate.shouldRefreshOn({
        botsExcludedSince: '2026-08-01T00:00:00Z',
        sharedChatSeparatedSince: null,
      }),
    ).toBe(true);
    expect(
      gate.shouldRefreshOn({
        botsExcludedSince: null,
        sharedChatSeparatedSince: '2026-08-01T00:00:00Z',
      }),
    ).toBe(true);
  });

  it('treats a null status (failed refetch) as incomplete', () => {
    const gate = new SetStatusFlushProbeGate();

    expect(gate.shouldRefreshOn(null)).toBe(true);
  });

  it('restores the full budget after reset()', () => {
    const gate = new SetStatusFlushProbeGate();
    const incomplete = { botsExcludedSince: null, sharedChatSeparatedSince: null };

    for (let i = 0; i < SET_STATUS_FLUSH_PROBE_LIMIT; i++) {
      gate.shouldRefreshOn(incomplete);
    }
    expect(gate.shouldRefreshOn(incomplete)).toBe(false);

    gate.reset();

    expect(gate.shouldRefreshOn(incomplete)).toBe(true);
  });
});
