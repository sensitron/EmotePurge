import { describe, expect, it } from 'vitest';

import { pluralKey } from './plural';

// Pure function, no TestBed needed. Worth a spec anyway: every plural label in the app routes
// through here, and the two sibling keys it produces must exist in both locale files — get the
// suffix wrong and Transloco silently renders the raw key.
describe('pluralKey', () => {
  it('picks the .one sibling for exactly one', () => {
    expect(pluralKey(1, 'emoteCount')).toBe('emoteCount.one');
  });

  it.each([0, 2, 42, 1000])('picks the .other sibling for %i', (count) => {
    expect(pluralKey(count, 'emoteCount')).toBe('emoteCount.other');
  });

  it('treats negatives as .other rather than throwing', () => {
    // No caller passes a negative today, but returning a key is the harmless direction: a missing
    // translation shows a key, an exception blanks the page.
    expect(pluralKey(-1, 'emoteCount')).toBe('emoteCount.other');
  });

  it('keeps dotted base keys intact', () => {
    expect(pluralKey(3, 'voting.detail.lowParticipation')).toBe(
      'voting.detail.lowParticipation.other',
    );
  });
});
