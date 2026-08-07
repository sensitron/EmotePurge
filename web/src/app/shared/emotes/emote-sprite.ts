import { NgOptimizedImage } from '@angular/common';
import { Component, ElementRef, computed, effect, input, signal, viewChild } from '@angular/core';

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
 * Not wrapped around that plate on purpose: the six call sites size and position their own container
 * (14, 12, 7 and 4 rem boxes, one of them the ballot's `app-sprite-cell-void`), so this owns the
 * picture and nothing else.
 */
@Component({
  selector: 'app-emote-sprite',
  imports: [NgOptimizedImage],
  template: `
    <img
      #img
      [ngSrc]="url()"
      [width]="size()"
      [height]="size()"
      alt=""
      [class]="spriteClass()"
      [class.opacity-40]="dimmed()"
      [style.visibility]="settled() ? null : 'hidden'"
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

  private readonly imageRef = viewChild.required<ElementRef<HTMLImageElement>>('img');

  private readonly loadedUrl = signal<string | null>(null);

  protected readonly settled = computed(() => this.loadedUrl() === this.url());

  constructor() {
    // Listeners are (re)bound by hand, one pair per url, instead of a template `(load)`/`(error)`
    // binding: a template event expression reads its arguments live at fire time, so
    // `onSettled(url())` would always read whatever url is *current when the event fires* — which,
    // for a stale response arriving after the pointer moved on, is already the next emote's url, so
    // the check "does this match the current url" is trivially true for every response, current or
    // stale alike. Binding fresh listeners whose closures capture the url they were attached for is
    // what makes the two distinguishable.
    //
    // The attach itself waits a microtask: the WHATWG spec queues an `<img>`'s `load`/`error` as an
    // async task even for an already-cached image, so a *genuine* completion can never fire in the
    // same synchronous turn as the url changing. Binding a tick late costs nothing for a real
    // completion, but it does mean a same-turn event has no listener to reach — which is exactly the
    // previous url's now-superseded pair, already detached below before that tick arrives.
    effect((onCleanup) => {
      const requestedUrl = this.url();
      const image = this.imageRef().nativeElement;
      const controller = new AbortController();

      const onLoad = (): void => this.loadedUrl.set(requestedUrl);
      const onError = (): void => this.loadedUrl.set(null);

      queueMicrotask(() => {
        if (controller.signal.aborted) {
          return;
        }
        image.addEventListener('load', onLoad, { signal: controller.signal });
        image.addEventListener('error', onError, { signal: controller.signal });
      });

      onCleanup(() => controller.abort());
    });
  }
}
