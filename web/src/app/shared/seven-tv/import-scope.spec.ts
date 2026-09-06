import { describe, expect, it } from 'vitest';

import { importScopeIsCurrent } from './import-scope';

describe('importScopeIsCurrent', () => {
  it('accepts only state where both halves name the current channel', () => {
    expect(importScopeIsCurrent('beta', 'beta', 'beta')).toBe(true);
  });

  it('rejects the window right after a same-route channel switch, before either answer lands', () => {
    // channelName() already follows the route while set status and totals still describe alpha.
    expect(importScopeIsCurrent('beta', 'alpha', 'alpha')).toBe(false);
  });

  it('rejects the half-updated states in both response orders', () => {
    // Set status answered first: the rows are still alpha's, so the captured scope would copy the
    // wrong emotes.
    expect(importScopeIsCurrent('beta', 'beta', 'alpha')).toBe(false);
    // Totals answered first: this is the order a plain "is it loading" flag misses, because loading
    // is already over while activeEmoteSetId() still points at alpha's set.
    expect(importScopeIsCurrent('beta', 'alpha', 'beta')).toBe(false);
  });

  it('rejects the initial state where nothing has answered yet', () => {
    expect(importScopeIsCurrent('beta', null, null)).toBe(false);
    expect(importScopeIsCurrent('beta', 'beta', null)).toBe(false);
    expect(importScopeIsCurrent('beta', null, 'beta')).toBe(false);
  });
});
