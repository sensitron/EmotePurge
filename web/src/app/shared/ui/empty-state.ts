import { Component, input } from '@angular/core';

/**
 * A deliberate empty state instead of a bare gray sentence: says why there is nothing here and,
 * via the projected content, what to do next (CTA button/link) — NN/g's "teachable moment".
 *
 * The emoji is gone (2026-08-06), and with it the `icon` input, so that no call site can keep one
 * by accident. It sat in a tinted square whose own comment described it as "echoing the landing's
 * feature-card icons" — cards that no longer exist, which made this the last place in the app still
 * pointing at them. Nine surfaces inherited it from here, which is also why removing it was worth
 * one commit of its own: a picture frame drawn around 🔍 or 📋 is not an icon system, it is the
 * absence of one.
 *
 * What replaces it is the same hairline-and-label device the atlas bands and the landing's stages
 * use. Left-aligned, because a centred column of text inside a left-aligned page reads as a
 * placeholder that someone forgot to fill in — which is precisely the wrong reading for a state
 * that is correct and expected.
 */
@Component({
  selector: 'app-empty-state',
  template: `
    <div class="flex flex-col items-start gap-2 border-t border-border py-8">
      <p class="text-sm font-medium text-fg-secondary">{{ title() }}</p>
      @if (description(); as text) {
        <p class="max-w-prose text-sm text-fg-muted">{{ text }}</p>
      }
      <div class="mt-2 empty:hidden"><ng-content /></div>
    </div>
  `,
})
export class EmptyState {
  readonly title = input.required<string>();
  readonly description = input<string | null>(null);
}
