import { Component, computed, input } from '@angular/core';

/**
 * The tone is a *meaning*, and since the light mode it is no longer even reliably the colour it used
 * to name: the tones were called `purple`/`emerald`/`red` back when the badge had one mode and the
 * name and the value were the same thing. They are not any more — `danger` is red-950/red-300 in
 * dark and red-50/red-700 in light — and a call site asking for `red` was asking for a hue it can no
 * longer be told the value of. What each tone *means* is in docs/UI-Designsprache.md §4.3.
 */
export type StatusBadgeTone = 'accent' | 'info' | 'success' | 'neutral' | 'warning' | 'danger';

/**
 * Every pair is a semantic wash plus its own foreground, which is what lets a near-black `-950` area
 * become a tinted `-50` one in light mode. Reading a nearly black rectangle on a white page as
 * "error" is what that inversion avoids — regardless of which hue it is.
 */
const TONE_CLASSES: Record<StatusBadgeTone, string> = {
  accent: 'bg-accent-wash text-accent-wash-fg',
  info: 'bg-info-wash text-info-fg',
  success: 'bg-success-wash text-success-fg',
  neutral: 'bg-neutral-wash text-neutral-fg',
  warning: 'bg-warning-wash text-warning-fg',
  danger: 'bg-danger-wash text-danger-fg',
};

/**
 * One pill shape for every status-like label (roles, bot state, session state) — replaces the
 * mix of ad-hoc pill markup and bare colored text the pages used before.
 */
@Component({
  selector: 'app-status-badge',
  template: `<span [class]="classes()"><ng-content /></span>`,
})
export class StatusBadge {
  readonly tone = input.required<StatusBadgeTone>();

  protected readonly classes = computed(
    () =>
      `inline-flex items-center rounded-full px-2 py-0.5 text-xs whitespace-nowrap ${TONE_CLASSES[this.tone()]}`,
  );
}
