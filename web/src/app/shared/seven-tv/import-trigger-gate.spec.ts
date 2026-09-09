import { describe, expect, it } from 'vitest';

import { ImportTriggerGateState, importTriggerDisabled } from './import-trigger-gate';

function state(overrides: Partial<ImportTriggerGateState> = {}): ImportTriggerGateState {
  return {
    hasActiveRun: false,
    importScopeCurrent: true,
    ...overrides,
  };
}

describe('importTriggerDisabled', () => {
  it('is enabled when nothing locks it', () => {
    expect(importTriggerDisabled(state())).toBe(false);
  });

  it('disables while any 7TV-writing run is active, not just a restore or import run', () => {
    expect(importTriggerDisabled(state({ hasActiveRun: true }))).toBe(true);
  });

  it("disables when importScopeCurrent is false — a mid-channel-switch capture would validate a file against channel A's set under B's name", () => {
    expect(importTriggerDisabled(state({ importScopeCurrent: false }))).toBe(true);
  });

  it('disables when both locks apply at once', () => {
    expect(importTriggerDisabled(state({ hasActiveRun: true, importScopeCurrent: false }))).toBe(
      true,
    );
  });
});
