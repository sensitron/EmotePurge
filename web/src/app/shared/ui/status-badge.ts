import { Component, computed, input } from '@angular/core';

export type StatusBadgeTone = 'purple' | 'blue' | 'emerald' | 'slate' | 'amber' | 'red';

/**
 * The tone names are historical (they were palette names when the badge only had one mode) and the
 * meaning attached to each lives in docs/UI-Designsprache.md §4.3. What changed here is only the
 * right-hand side: every pair is now a semantic wash + its own foreground, which is what lets a
 * near-black `-950` area become a tinted `-50` one in light mode. Reading a nearly black rectangle
 * on a white page as "error" is what the inversion avoids — regardless of which hue it is.
 */
const TONE_CLASSES: Record<StatusBadgeTone, string> = {
  purple: 'bg-accent-wash text-accent-wash-fg',
  blue: 'bg-info-wash text-info-fg',
  emerald: 'bg-success-wash text-success-fg',
  slate: 'bg-neutral-wash text-neutral-fg',
  amber: 'bg-warning-wash text-warning-fg',
  red: 'bg-danger-wash text-danger-fg',
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
