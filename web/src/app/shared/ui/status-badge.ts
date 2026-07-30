import { Component, computed, input } from '@angular/core';

export type StatusBadgeTone = 'purple' | 'blue' | 'emerald' | 'slate' | 'amber' | 'red';

const TONE_CLASSES: Record<StatusBadgeTone, string> = {
  purple: 'bg-purple-950 text-purple-300',
  blue: 'bg-blue-950 text-blue-300',
  emerald: 'bg-emerald-950 text-emerald-300',
  slate: 'bg-slate-800 text-slate-300',
  amber: 'bg-amber-950 text-amber-300',
  red: 'bg-red-950 text-red-300',
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
    () => `inline-flex items-center rounded-full px-2 py-0.5 text-xs whitespace-nowrap ${TONE_CLASSES[this.tone()]}`,
  );
}
