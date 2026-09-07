import { describe, expect, it } from 'vitest';

import { FileImportTriggerGateState, fileImportTriggerDisabled } from './file-import-trigger-gate';

function state(overrides: Partial<FileImportTriggerGateState> = {}): FileImportTriggerGateState {
  return {
    hasActiveRun: false,
    importScopeCurrent: true,
    ...overrides,
  };
}

describe('fileImportTriggerDisabled', () => {
  it('is enabled when nothing locks it', () => {
    expect(fileImportTriggerDisabled(state())).toBe(false);
  });

  it('disables while any 7TV-writing run is active, not just a restore or import run', () => {
    expect(fileImportTriggerDisabled(state({ hasActiveRun: true }))).toBe(true);
  });

  it("disables when importScopeCurrent is false — a mid-channel-switch capture would validate a file against channel A's set under B's name", () => {
    expect(fileImportTriggerDisabled(state({ importScopeCurrent: false }))).toBe(true);
  });

  it('disables when both locks apply at once', () => {
    expect(
      fileImportTriggerDisabled(state({ hasActiveRun: true, importScopeCurrent: false })),
    ).toBe(true);
  });
});
