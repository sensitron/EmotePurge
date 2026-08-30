import { describe, expect, it } from 'vitest';

import { emoteVariantUrl } from './emote-image-loader';

describe('emoteVariantUrl', () => {
  it('returns the url unchanged when width is not set', () => {
    expect(emoteVariantUrl('https://cdn.7tv.app/emote/aaa/4x.webp', undefined)).toBe(
      'https://cdn.7tv.app/emote/aaa/4x.webp',
    );
  });

  it('passes through a url that has no 4x suffix at all, regardless of width', () => {
    expect(emoteVariantUrl('https://cdn.7tv.app/emote/aaa/2x.webp', 64)).toBe(
      'https://cdn.7tv.app/emote/aaa/2x.webp',
    );
  });

  it('never rewrites the animated 2x fallback down to 1x, even for the 28 px readout line', () => {
    // This is the case the doc comment calls out by name: rewriting it would undo the sidecar's own
    // 4x -> 2x downgrade for the hover animation (commit 1001d0b).
    expect(emoteVariantUrl('https://cdn.7tv.app/emote/aaa/2x.webp', 28)).toBe(
      'https://cdn.7tv.app/emote/aaa/2x.webp',
    );
  });

  describe('a still 4x.webp url', () => {
    it.each([
      [32, '1x.webp'],
      [56, '2x.webp'],
      [64, '2x.webp'],
      [65, '3x.webp'],
      [96, '3x.webp'],
      [97, '4x.webp'],
      [128, '4x.webp'],
    ])('rewrites to %s -> .../%s', (width, expected) => {
      expect(emoteVariantUrl('https://cdn.7tv.app/emote/aaa/4x.webp', width)).toBe(
        `https://cdn.7tv.app/emote/aaa/${expected}`,
      );
    });
  });

  describe('an animated 4x_static.webp url', () => {
    it.each([
      [32, '1x_static.webp'],
      [64, '2x_static.webp'],
      [96, '3x_static.webp'],
      [128, '4x_static.webp'],
    ])('rewrites to %s -> .../%s', (width, expected) => {
      expect(emoteVariantUrl('https://cdn.7tv.app/emote/aaa/4x_static.webp', width)).toBe(
        `https://cdn.7tv.app/emote/aaa/${expected}`,
      );
    });
  });
});
