import { Component, computed, input } from '@angular/core';

export type NoticeVariant = 'info' | 'warning' | 'error';

/**
 * `info` gets the neutral WASH, not the card surface it used to share. `bg-slate-900` was the exact
 * colour of `.app-card`, so an info banner placed on a card was invisible — it only ever looked like
 * a banner because the pages it sat on were darker still. In light mode (card = white) it would have
 * disappeared outright. One step away from the surface in both directions fixes both.
 */
const VARIANT_CLASSES: Record<NoticeVariant, string> = {
  info: 'bg-neutral-wash text-neutral-fg',
  warning: 'bg-warning-wash text-warning-fg',
  error: 'bg-danger-wash text-danger-fg',
};

/**
 * One shape for every page-level notice — before, warnings ranged from a styled box with CTA
 * (reauth) to bare colored paragraphs (helix down), and error boxes were copy-pasted markup.
 * `role` follows the variant: errors announce as alerts, the rest as polite status.
 * Optional action slot: `<button notice-action ...>` renders right-aligned inside the banner.
 */
@Component({
  selector: 'app-notice-banner',
  template: `
    <div [class]="classes()" [attr.role]="variant() === 'error' ? 'alert' : 'status'">
      <div class="min-w-0 flex-1"><ng-content /></div>
      <ng-content select="[notice-action]" />
    </div>
  `,
})
export class NoticeBanner {
  readonly variant = input.required<NoticeVariant>();

  protected readonly classes = computed(
    () =>
      `flex flex-wrap items-center justify-between gap-3 rounded-md px-4 py-3 text-sm ${VARIANT_CLASSES[this.variant()]}`,
  );
}
