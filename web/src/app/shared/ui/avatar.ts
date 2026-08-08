import { NgOptimizedImage } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';

/**
 * Round picture carrier with a monogram fallback, sized in px rather than by class because the one
 * thing it must never do is change size: it sits in a 56 px header, and a plate that grows when the
 * picture arrives is a layout jump in the app frame — the one place the design language rules out
 * entirely. So the plate is painted first at its final size and the picture appears inside it.
 *
 * `settled` is keyed on url identity, not on a "has loaded once" boolean — the same pattern
 * `emote-sprite.ts` uses and for the same reason: the node is reused across url changes, so the
 * signal has to name *which* url it belongs to. Here that matters less than on a hovered atlas, but
 * it costs nothing and keeps one pattern in the repo instead of two.
 *
 * An empty `displayName` renders the plate with no letter at all. That is not a degenerate case but
 * a state the account menu needs: before /api/auth/me answers, the trigger must hold its exact
 * final shape without claiming to know whose account it is.
 *
 * Decorative throughout: `aria-hidden` on the plate, `alt=""` on the picture. The accessible name
 * belongs to whatever interactive element wraps this.
 */
@Component({
  selector: 'app-avatar',
  imports: [NgOptimizedImage],
  template: `
    <span
      aria-hidden="true"
      class="relative flex shrink-0 items-center justify-center overflow-hidden rounded-full bg-accent-selected font-medium text-on-accent"
      [style.width.px]="size()"
      [style.height.px]="size()"
      [style.font-size.px]="monogramSize()"
    >
      {{ settled() ? '' : monogram() }}
      @if (imageUrl(); as url) {
        <img
          [ngSrc]="url"
          [width]="size()"
          [height]="size()"
          alt=""
          class="absolute inset-0 h-full w-full object-cover"
          [style.visibility]="settled() ? null : 'hidden'"
          (load)="loadedUrl.set(url)"
          (error)="loadedUrl.set(null)"
        />
      }
    </span>
  `,
})
export class Avatar {
  readonly displayName = input.required<string>();
  readonly imageUrl = input<string | null>(null);
  /**
   * Edge length in px. Must be constant per call site — NgOptimizedImage objects to width/height
   * changing after init.
   */
  readonly size = input(32);

  protected readonly loadedUrl = signal<string | null>(null);

  protected readonly settled = computed(
    () => this.loadedUrl() !== null && this.loadedUrl() === this.imageUrl(),
  );
  protected readonly monogram = computed(() => this.displayName().trim().slice(0, 1).toUpperCase());
  protected readonly monogramSize = computed(() => Math.round(this.size() * 0.45));
}
