import { NgOptimizedImage } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';

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
 */
@Component({
  selector: 'app-emote-sprite',
  imports: [NgOptimizedImage],
  template: `
    <img
      [ngSrc]="url()"
      [width]="size()"
      [height]="size()"
      alt=""
      [class]="spriteClass()"
      [class.opacity-40]="dimmed()"
      [style.visibility]="settled() ? null : 'hidden'"
      (load)="loadedUrl.set(url())"
      (error)="loadedUrl.set(null)"
    />
  `,
  host: { class: 'contents' },
})
export class EmoteSprite {
  readonly url = input.required<string>();
  /**
   * Edge length in px, for the intrinsic size NgOptimizedImage requires. Must be constant per call
   * site — the directive objects to width/height changing after init.
   */
  readonly size = input.required<number>();
  readonly spriteClass = input('h-full w-full object-contain p-1');
  /** Archived ballot members, which stay listed but read as spent. */
  readonly dimmed = input(false);

  protected readonly loadedUrl = signal<string | null>(null);

  protected readonly settled = computed(() => this.loadedUrl() === this.url());
}
