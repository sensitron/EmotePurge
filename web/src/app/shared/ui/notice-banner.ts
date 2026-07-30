import { Component, computed, input } from '@angular/core';

export type NoticeVariant = 'info' | 'warning' | 'error';

const VARIANT_CLASSES: Record<NoticeVariant, string> = {
  info: 'bg-slate-900 text-slate-300',
  warning: 'bg-amber-950 text-amber-300',
  error: 'bg-red-950 text-red-300',
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
    () => `flex flex-wrap items-center justify-between gap-3 rounded-md px-4 py-3 text-sm ${VARIANT_CLASSES[this.variant()]}`,
  );
}
