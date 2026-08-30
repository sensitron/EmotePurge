import { IMAGE_LOADER, NgOptimizedImage } from '@angular/common';
import { Component, computed, effect, input, output, signal } from '@angular/core';

import { emoteSpriteImageLoader } from './emote-image-loader';

/**
 * One emote sprite, drawn only once it belongs to the emote next to it.
 *
 * The sidecar and the readout line bind their `<img>` on a node that is never rebuilt — their `@if`
 * is a null check that, thanks to the `order[0]` fallback, never actually goes null. So a hover only
 * rebound the attribute: every text node beside it re-rendered synchronously while the browser kept
 * painting the previous, already-decoded bitmap until the new one arrived. A wrong picture next to
 * right numbers is misinformation, and on a virtualized, lazily-loading atlas the window in which it
 * happens is real rather than theoretical.
 *
 * Hidden rather than removed: an `<img>` outside the DOM never starts loading, and `visibility`
 * keeps the box so revealing it costs no layout shift. What shows through meanwhile is the caller's
 * `app-sprite-cell` plate, which until now was never visible at all because the stale image covered
 * it. A failed load stays hidden for the same reason — a broken-image glyph says less than the plate.
 *
 * `settled` is keyed on url identity rather than a bare "has *a* load fired" boolean: the same node
 * is reused for every emote the pointer passes over (see above), so `loadedUrl` has to name *which*
 * url it belongs to, not just that some load once happened. Reassigning `[ngSrc]` per the HTML "update
 * the image data" algorithm aborts whatever request was still in flight and discards its queued
 * tasks — a superseded request never reaches `load`/`error` on this element — so once the url moves
 * on, `loadedUrl` staying at the old value is exactly what flips this back to hidden, with no extra
 * bookkeeping needed to reject a stale response that can't arrive in the first place.
 *
 * Not wrapped around that plate on purpose: the six call sites size and position their own container
 * (14, 12, 7 and 4 rem boxes, one of them the ballot's `app-sprite-cell-void`), so this owns the
 * picture and nothing else.
 *
 * `ngSrcset`/`sizes`: width descriptors matching 7TV's four variant heights (32/64/96/128 px, see
 * `VARIANT_HEIGHTS` and the 4x cap in `emote-image-loader.ts`), paired with `sizes` bound to this
 * component's own edge length so the browser can pick the right one. `ngSrcset` is set explicitly
 * rather than left to `NgOptimizedImage`'s automatic width-based srcset (a component-local
 * `IMAGE_CONFIG` with these same numbers as `breakpoints`, relying on `sizes` alone to drive it):
 * that automatic path is gated by `assertNoComplexSizes`, which throws NG02952 for *any* `sizes`
 * value containing a bare px number — it exists specifically to stop a fixed-size `sizes` string
 * from being paired with the auto-generated srcset, since that path assumes a viewport-relative
 * layout (`sizes="50vw"`), which this component's genuinely fixed edge length is not. Supplying
 * `ngSrcset` explicitly sidesteps that check — `ng_optimized_image.ts` only calls
 * `assertNoComplexSizes` `if (!this.ngSrcset)` — and matches Angular's own documented pattern for a
 * fixed-size image that still wants width descriptors: `<img ngSrc="hero.jpg" ngSrcset="100w, 200w,
 * 300w" sizes="50vw" />`. An `IMAGE_CONFIG` breakpoints array would be silently ignored here anyway:
 * `getResponsiveSrcset()`, the only place that reads it, never runs once `ngSrcset` is set (see
 * `updateSrcAndSrcset()`).
 *
 * `loading="eager"`: not about *when* the fetch fires — measured separately that `eager` changes
 * nothing there, Chromium already requests `lazy` images once they're ~1250px from the viewport
 * (the CDK virtual-scroll buffer is smaller than that), 184 of 185 rendered images were requested
 * with no scrolling at all. It's here because `setHostAttributes` in `@angular/common` only
 * prepends `auto, ` to `sizes` for `loading="lazy"` (its default). `auto` defers the `sizes`
 * lookup to layout time; until a freshly rendered `<img>` has a box, the browser can't resolve it
 * and grabs the largest `ngSrcset` candidate instead, then re-fetches the right one once layout
 * settles — a second request per image. Measured on the 649-emote usage page at DPR 1: `lazy` cost
 * 680 requests for 650 images (30 of them a redundant 4x fetch) at 4.21 MB; `eager` cost 651
 * requests, all 2x, at 3.51 MB. See `docs/Untersuchung-Emote-Bildladen-2026-08-29.md`. Reverting
 * this to `lazy` brings the double-fetch back — don't, without re-checking that doc.
 */
@Component({
  selector: 'app-emote-sprite',
  imports: [NgOptimizedImage],
  template: `
    <img
      [ngSrc]="url()"
      [width]="size()"
      [height]="size()"
      ngSrcset="32w, 64w, 96w, 128w"
      [sizes]="sizes() ?? size() + 'px'"
      loading="eager"
      alt=""
      [class]="spriteClass()"
      [class.opacity-40]="dimmed()"
      [style.visibility]="settled() ? null : 'hidden'"
      (load)="loadedUrl.set(url())"
      (error)="loadedUrl.set(null)"
    />
  `,
  host: { class: 'contents' },
  // Scoped to this component's element injector, not registered app-wide: `shared/ui/avatar.ts`
  // uses `NgOptimizedImage` too, for Twitch profile pictures, and a global 7TV-shaped loader would
  // rewrite those urls into nonsense.
  providers: [{ provide: IMAGE_LOADER, useValue: emoteSpriteImageLoader }],
})
export class EmoteSprite {
  readonly url = input.required<string>();
  /**
   * Edge length in px, for the intrinsic size NgOptimizedImage requires and for the `sizes`
   * attribute that tells the browser which `ngSrcset` candidate to fetch. Must be constant per call
   * site — the directive objects to width/height/sizes changing after init
   * (`assertNoPostInitInputChange`), which this satisfies the same way it already does for
   * width/height: every call site passes a literal.
   */
  readonly size = input.required<number>();
  /**
   * Static override for the `sizes` attribute NgOptimizedImage reads to pick the right `ngSrcset`
   * candidate. Falls back to `size() + 'px'` when omitted, which is right for every call site whose
   * rendered edge length truly is a constant `size`.
   *
   * A call site that draws itself at more than one edge length across a *container*-width
   * breakpoint (the ballot cell — `CELL_WIDE_PX`/`CELL_NARROW_PX` in
   * `vote-session-detail-page.ts`) can't just bind this to that computed width: `size`/`sizes` are
   * once-per-init like `width`/`height` (`assertNoPostInitInputChange`), and that computed changes
   * exactly at the breakpoint. What such a call site passes instead is a static CSS media-query
   * string mirroring the same breakpoint in viewport terms, e.g. `"(min-width: 640px) 64px,
   * 96px"`. The `640` is not `NARROW_BELOW_PX` (600) itself: that threshold measures the *sheet*,
   * the query measures the *window*, and the shell's `px-4` (`app-shell.ts`) sits between them —
   * the sheet only reaches 600px once the window is 632px wide. Rounded up to Tailwind's `sm`
   * (640) rather than down, so the remaining slack picks a size one step too *large* (a few
   * wasted bytes) instead of one step too small (a stretched image). It stays an approximation
   * even so — `atlasColumns` in `atlas-grid.ts` measures the *container* the cells actually sit in
   * (the sheet can share the row with a sidecar from `lg` up),
   * while a media query only ever sees the *viewport* — but accounting for the shell padding is
   * what keeps it from being off in the wrong direction, which is the mistake this once was.
   */
  readonly sizes = input<string>();
  readonly spriteClass = input('h-full w-full object-contain p-1');
  /** Archived ballot members, which stay listed but read as spent. */
  readonly dimmed = input(false);

  /**
   * Fires the url once *this* sprite has settled on it (the moment `settled` below flips true) —
   * an identity, not a bare "ready" boolean, for the same reason `loadedUrl` is keyed on the url
   * rather than a flag: a caller stacking more than one sprite on the same url (EmoteSpriteAnimated)
   * needs to tell an emission for the url it currently cares about apart from a stale one still in
   * flight for a url it has since moved past.
   */
  readonly settledUrl = output<string>();

  protected readonly loadedUrl = signal<string | null>(null);

  protected readonly settled = computed(() => this.loadedUrl() === this.url());

  constructor() {
    effect(() => {
      if (this.settled()) {
        this.settledUrl.emit(this.url());
      }
    });
  }
}
