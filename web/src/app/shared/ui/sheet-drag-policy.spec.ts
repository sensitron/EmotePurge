import { describe, expect, it } from 'vitest';

import {
  SHEET_DISMISS_DISTANCE_PX,
  SHEET_DISMISS_VELOCITY_PX_PER_MS,
  SHEET_MIN_TRAVEL_PX,
  shouldDismiss,
} from './sheet-drag-policy';

describe('shouldDismiss', () => {
  it('keeps the sheet when it was barely moved', () => {
    expect(shouldDismiss(10, 0)).toBe(false);
  });

  it('dismisses on distance alone, however slowly it was dragged', () => {
    expect(shouldDismiss(SHEET_DISMISS_DISTANCE_PX, 0)).toBe(true);
  });

  it('dismisses a short but fast flick', () => {
    // The gesture people actually make: a quick flick down, released long before 96 px.
    expect(shouldDismiss(SHEET_MIN_TRAVEL_PX, SHEET_DISMISS_VELOCITY_PX_PER_MS)).toBe(true);
  });

  it('ignores speed below the travel floor', () => {
    // Otherwise a 4 px twitch during a tap — which is fast, because it is short — would close it.
    expect(shouldDismiss(SHEET_MIN_TRAVEL_PX - 1, 99)).toBe(false);
  });

  it('never dismisses on an upward or zero drag', () => {
    expect(shouldDismiss(0, 5)).toBe(false);
    expect(shouldDismiss(-200, 5)).toBe(false);
  });
});
