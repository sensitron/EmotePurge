import { FormControl } from '@angular/forms';
import { describe, expect, it } from 'vitest';

import { channelNameValidator, normalizeChannelName } from './channel-name';

describe('normalizeChannelName', () => {
  it('trims and lower-cases, matching the server-side ChannelName.Normalize', () => {
    expect(normalizeChannelName('  HandOfBlood ')).toBe('handofblood');
  });
});

describe('channelNameValidator', () => {
  const validate = (value: string) =>
    channelNameValidator(new FormControl(value, { nonNullable: true }));

  it('accepts a mixed-case name because it validates the normalized value', () => {
    // The regression this guards: matching a lower-case-only pattern against the *raw* input
    // rejected "HandOfBlood" in the browser while the Api happily accepted it.
    expect(validate('HandOfBlood')).toBeNull();
    expect(validate('  sensitron  ')).toBeNull();
  });

  it('accepts digits and underscores', () => {
    expect(validate('cool_streamer_42')).toBeNull();
  });

  it('rejects too-short, too-long and illegal-character names', () => {
    expect(validate('abc')).toEqual({ channelName: true });
    expect(validate('a'.repeat(26))).toEqual({ channelName: true });
    expect(validate('has spaces')).toEqual({ channelName: true });
    expect(validate('hyphen-name')).toEqual({ channelName: true });
    expect(validate('')).toEqual({ channelName: true });
  });
});
