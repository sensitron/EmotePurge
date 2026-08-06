import { Directive, computed, input } from '@angular/core';

export type ButtonVariant =
  'primary' | 'neutral' | 'outline' | 'danger' | 'danger-quiet' | 'danger-solid';
export type ButtonSize = 'md' | 'lg';

const VARIANT_CLASSES: Record<ButtonVariant, string> = {
  primary: 'bg-accent-solid font-medium text-on-accent hover:bg-accent-solid-hover',
  neutral: 'bg-surface-inset text-fg-body hover:bg-surface-inset-hover',
  outline: 'border border-border-strong text-fg-secondary hover:bg-surface-inset',
  // Two destructive intensities on purpose, keyed to flow position rather than severity:
  // outline for the triggering button that sits in page context next to other controls
  // (leave channel, delete one session, open the purge dialog), solid for the executing
  // confirm button inside a dialog and for a page's primary destructive CTA (mass-delete).
  // See docs/UI-Designsprache.md §4.2.
  danger: 'border border-danger-solid text-danger-fg hover:bg-danger-wash',
  // Third destructive tier, added 2026-08-06 when the vote-session list lost its cards: the same
  // outline button repeated once per paged row turned into twenty red rectangles running down the
  // page, which made the rarest action on it the loudest element. Keyed to repetition at page
  // scale, not to a lower severity — the hover wash and the confirm dialog behind it are unchanged,
  // only the permanent red box is gone. A single triggering button in page context keeps `danger`
  // (leave channel). The admin lists repeat theirs too, but a handful of times rather than twenty,
  // and purge/revoke are heavier than deleting a vote session — they stay on `danger` until those
  // pages are rebuilt and the trade can be judged in context rather than by rule.
  'danger-quiet': 'text-danger-fg hover:bg-danger-wash',
  'danger-solid': 'bg-danger-solid font-medium text-on-accent hover:bg-danger-solid-hover',
};

/**
 * A real disabled appearance rather than `disabled:opacity-50`. Half-opacity white over a
 * half-opacity purple over white lands at 2,3:1 in light mode — that reads as broken rather than as
 * unavailable. Flattening to the inset surface plus the disabled text token says the same thing at
 * a contrast that survives both modes, and it does not depend on what is behind the button.
 * (Disabled controls are exempt from 1.4.3, but "exempt" is not a licence to look defective.)
 */
const DISABLED_CLASSES =
  'disabled:border-transparent disabled:bg-surface-inset disabled:text-fg-disabled';

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
      `rounded-md text-sm whitespace-nowrap transition ${DISABLED_CLASSES} ${SIZE_CLASSES[this.buttonSize()]} ${VARIANT_CLASSES[this.appButton()]}`,
  );
}
