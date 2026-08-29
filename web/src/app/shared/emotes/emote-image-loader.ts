import type { ImageLoader, ImageLoaderConfig } from '@angular/common';

/**
 * Rewrites a stored 7TV emote url down to the smallest variant that still covers the requested
 * pixel width, for `NgOptimizedImage`'s `IMAGE_LOADER` token.
 *
 * Every emote is stored as its 4x still (see `animatedEmoteUrl` above for why), which is ~128 px
 * tall. The atlas renders it into a 64 px cell — the browser downscales, but it still has to fetch
 * and decode the full 4x bytes first. Measured: 5.75 MB for 649 emotes at 4x, and the bottleneck is
 * the browser's own request queue (p90 5 s wait before a request is even sent), so fewer bytes per
 * emote shortens that queue directly. `EmoteSprite` provides this loader so `NgOptimizedImage`
 * builds a density `srcset` (1x/2x descriptors) and a normal-density display loads the 2x variant
 * instead of the 4x one it would otherwise fetch for both descriptors.
 *
 * 7TV serves four variants — `1x`/`2x`/`3x`/`4x`, each roughly 32/64/96/128 px tall, as `<n>x.webp`
 * and, for animated emotes, also `<n>x_static.webp`. The variant chosen is the smallest one whose
 * height is still >= the requested width (cells are square), capped at 4x.
 *
 * Only urls ending in the stored 4x suffix are rewritten. That is deliberate, not incidental:
 * `animatedEmoteUrl` already downgrades the sidecar's hover animation to a fixed `2x.webp` url for
 * its own, unrelated reason (commit 1001d0b) — the animation is fetched once, for a single emote
 * the pointer dwelt on, not for every cell in the atlas, so a 4x/2x tradeoff never applied to it in
 * the first place. That url doesn't match either suffix here, so it passes through untouched
 * regardless of the requested width — which matters, because rewriting it back down (e.g. to 1x for
 * the 28 px readout line) would blur an animation the sidecar deliberately already fetches at 2x.
 * Anything else — a url this component has never seen — passes through unchanged too.
 */
const STILL_STATIC_SUFFIX = '/4x_static.webp';
const STILL_SUFFIX = '/4x.webp';

/** `[variant, height]` pairs below the 4x cap, ordered smallest first. */
const VARIANT_HEIGHTS: ReadonlyArray<readonly [variant: string, height: number]> = [
  ['1x', 32],
  ['2x', 64],
  ['3x', 96],
];

export function emoteVariantUrl(src: string, width: number | undefined): string {
  if (width === undefined) {
    return src;
  }
  if (src.endsWith(STILL_STATIC_SUFFIX)) {
    return `${src.slice(0, -STILL_STATIC_SUFFIX.length)}/${variantFor(width)}_static.webp`;
  }
  if (src.endsWith(STILL_SUFFIX)) {
    return `${src.slice(0, -STILL_SUFFIX.length)}/${variantFor(width)}.webp`;
  }
  return src;
}

/** `NgOptimizedImage`-shaped adapter around `emoteVariantUrl`, for `EmoteSprite`'s `IMAGE_LOADER` provider. */
export const emoteSpriteImageLoader: ImageLoader = (config: ImageLoaderConfig) =>
  emoteVariantUrl(config.src, config.width);

function variantFor(width: number): string {
  for (const [variant, height] of VARIANT_HEIGHTS) {
    if (width <= height) {
      return variant;
    }
  }
  return '4x';
}
