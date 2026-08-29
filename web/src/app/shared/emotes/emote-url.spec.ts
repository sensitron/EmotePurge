import { describe, expect, it } from 'vitest';

import { animatedEmoteUrl } from './emote-url';

describe('animatedEmoteUrl', () => {
  it('swaps the stored 4x still for the 2x animation', () => {
    expect(
      animatedEmoteUrl('https://cdn.7tv.app/emote/01FFWH9WV80000JT8GHDKHJNZC/4x_static.webp'),
    ).toBe('https://cdn.7tv.app/emote/01FFWH9WV80000JT8GHDKHJNZC/2x.webp');
  });

  it('leaves a still emote alone, because it has no animation to upgrade to', () => {
    const still = 'https://cdn.7tv.app/emote/01F6MZGCNG000255K4X1K7NTHR/4x.webp';

    expect(animatedEmoteUrl(still)).toBe(still);
  });

  it('leaves an empty url empty rather than inventing a request', () => {
    expect(animatedEmoteUrl('')).toBe('');
  });

  it('leaves a url it does not recognise untouched', () => {
    const other = 'https://cdn.7tv.app/emote/01F6MZGCNG000255K4X1K7NTHR/3x.avif';

    expect(animatedEmoteUrl(other)).toBe(other);
  });
});
