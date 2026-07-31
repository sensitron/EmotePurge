import { Directive, computed, input } from '@angular/core';

export type ButtonVariant = 'primary' | 'neutral' | 'outline' | 'danger' | 'danger-solid';
export type ButtonSize = 'md' | 'lg';

const VARIANT_CLASSES: Record<ButtonVariant, string> = {
  primary: 'bg-purple-600 font-medium text-white hover:bg-purple-500',
  neutral: 'bg-slate-800 text-slate-200 hover:bg-slate-700',
  outline: 'border border-slate-700 text-slate-300 hover:bg-slate-800',
  // Two destructive intensities on purpose, keyed to flow position rather than severity:
  // outline for the triggering button that sits in page context next to other controls
  // (leave channel, delete one session, open the purge dialog), solid for the executing
  // confirm button inside a dialog and for a page's primary destructive CTA (mass-delete).
  // See docs/UI-Designsprache.md §4.2.
  danger: 'border border-red-800 text-red-400 hover:bg-red-950',
  'danger-solid': 'bg-red-800 font-medium text-white hover:bg-red-700',
};

const SIZE_CLASSES: Record<ButtonSize, string> = {
  md: 'px-3 py-1.5',
  lg: 'px-4 py-2',
};

/**
 * The app's single button look: `<button appButton="primary">`. Replaces the utility chains that
 * were copy-pasted across templates. Element-specific layout classes (ml-auto, relative z-10, …)
 * stay on the element's own class attribute — Angular merges both.
 */
@Directive({
  selector: '[appButton]',
  host: { '[class]': 'classes()' },
})
export class Button {
  readonly appButton = input.required<ButtonVariant>();
  readonly buttonSize = input<ButtonSize>('md');

  protected readonly classes = computed(
    () =>
      `rounded-md text-sm whitespace-nowrap transition disabled:opacity-50 ${SIZE_CLASSES[this.buttonSize()]} ${VARIANT_CLASSES[this.appButton()]}`,
  );
}
