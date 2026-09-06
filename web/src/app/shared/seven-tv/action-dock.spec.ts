import { describe, expect, it } from 'vitest';

import { ActionDockState, actionDockHasContent } from './action-dock';

function state(overrides: Partial<ActionDockState> = {}): ActionDockState {
  return {
    hasActiveSet: true,
    markedCount: 0,
    deleteShown: false,
    restoreShown: false,
    importShown: false,
    ...overrides,
  };
}

describe('actionDockHasContent', () => {
  it('is false with nothing marked and no run', () => {
    expect(actionDockHasContent(state())).toBe(false);
  });

  it('shows the marking half for a selection, a delete run or a restore run', () => {
    expect(actionDockHasContent(state({ markedCount: 1 }))).toBe(true);
    expect(actionDockHasContent(state({ deleteShown: true }))).toBe(true);
    expect(actionDockHasContent(state({ restoreShown: true }))).toBe(true);
  });

  it('stays empty without an active set, however much is marked', () => {
    // The regression: a transient 5xx on the set-status request leaves the grid standing with no
    // set, and the marking half is gated on that set — so a click on a cell used to mount an
    // accent-framed bar with nothing inside it, plus the page's pb-40 of empty space.
    expect(actionDockHasContent(state({ hasActiveSet: false, markedCount: 3 }))).toBe(false);
    expect(actionDockHasContent(state({ hasActiveSet: false, deleteShown: true }))).toBe(false);
    expect(actionDockHasContent(state({ hasActiveSet: false, restoreShown: true }))).toBe(false);
  });

  it('shows an import run without an active set — the import half has no set gate (R9)', () => {
    expect(actionDockHasContent(state({ hasActiveSet: false, importShown: true }))).toBe(true);
  });
});
