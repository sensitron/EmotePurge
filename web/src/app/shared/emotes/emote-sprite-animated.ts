import { Component, computed, effect, input, signal } from '@angular/core';

import { EmoteSprite } from './emote-sprite';
import { animatedEmoteUrl } from './emote-url';

/**
 * One emote, still first and animated a moment later — for the surfaces that show a single emote.
 *
 * Every emote is stored as its 4x still, because the atlas draws hundreds at once (the reasoning
 * lives on `animatedEmoteUrl`). The sidecar and the readout line show one at a time, so they can
 * afford the animation. They just must not pay for it on a pointer that is merely passing: the
 * atlas is a dense grid, and one sweep across it makes this component see dozens of emotes. Hence
 * the dwell — the animation is fetched only for an emote the pointer actually stopped on.
 *
 * Stacked rather than swapped. Rebinding one `<img>` to the animated url would hide the sprite
 * until the new bytes arrive (EmoteSprite keeps an unloaded picture invisible, deliberately), so
 * the emote would blink out under the cursor for as long as a 1.2 MB animation takes. Here the
 * still holds the box — already painted, since the atlas loaded that exact url — and the animation
 * lies on top, revealing itself through EmoteSprite's own settle logic once it has decoded.
 *
 * `upgraded` is keyed on url identity rather than a bare boolean, the same way `settled` is, and
 * for a sharper reason: a boolean left true from the previous emote would let the *next* emote's
 * animation mount instantly, skipping the dwell it was supposed to earn. Keying it means a url
 * change withdraws the overlay synchronously, with no dependency on when the effect happens to run.
 */
@Component({
  selector: 'app-emote-sprite-animated',
  imports: [EmoteSprite],
  template: `
    <span class="relative block h-full w-full">
      <app-emote-sprite [url]="url()" [size]="size()" [spriteClass]="spriteClass()" />
      @if (upgraded()) {
        <app-emote-sprite
          [url]="animated()"
          [size]="size()"
          [spriteClass]="spriteClass() + ' absolute inset-0'"
        />
      }
    </span>
  `,
  host: { class: 'contents' },
})
export class EmoteSpriteAnimated {
  /** The stored still url, exactly as the atlas draws it. */
  readonly url = input.required<string>();
  readonly size = input.required<number>();
  readonly spriteClass = input('h-full w-full object-contain p-1');

  /**
   * How long the emote has to stay put before its animation is worth fetching. 200 ms is below the
   * point where a deliberate stop feels delayed, and above the time a sweeping pointer spends on
   * any one cell.
   */
  readonly dwellMs = input(200);

  protected readonly animated = computed(() => animatedEmoteUrl(this.url()));

  private readonly upgradedUrl = signal<string | null>(null);

  protected readonly upgraded = computed(() => this.upgradedUrl() === this.animated());

  constructor() {
    effect((onCleanup) => {
      const target = this.animated();
      // A still emote's animated url is its own: there is nothing to fetch, and mounting a second
      // <img> on it would duplicate a request for a picture already on screen.
      if (target === this.url()) {
        return;
      }

      const handle = setTimeout(() => this.upgradedUrl.set(target), this.dwellMs());
      // Runs before the next effect pass and on destroy, so an emote the pointer left behind never
      // fires its request.
      onCleanup(() => clearTimeout(handle));
    });
  }
}
