import { Component, computed, input } from '@angular/core';

import { StateDot } from './state-dot';
import { StatusBadge, StatusBadgeTone } from './status-badge';

export type HealthTone = 'ok' | 'idle' | 'warning' | 'danger';

/** Only the two states that want an admin's attention become a coloured pill. */
const BADGE_TONES: Partial<Record<HealthTone, StatusBadgeTone>> = {
  warning: 'warning',
  danger: 'danger',
};

/**
 * The status marker for a subsystem that reports its own health.
 *
 * The admin area had drifted into badging every one of them, which is the same mistake the overview
 * made with roles: a green "connected" pill on Twitch IRC, on the 7TV socket, on the roster, on the
 * batch flush and on the SSE stream means a healthy page carries five coloured rectangles — and a
 * broken subsystem has to compete with four of them to be noticed. The whole reason to look at this
 * page is to find the one that is not normal.
 *
 * So normal is quiet: `ok` and `idle` render as a dot plus a word (the same shape a list row's own
 * state uses), and only `warning` and `danger` earn the pill. A healthy monitoring page now has no
 * coloured badge on it at all, which makes the first one that appears the loudest thing on screen
 * without anything having to shout. That is also why this is not "a badge with fewer colours": the
 * verdict is delivered by absence, and absence only works if nothing else is spending it.
 *
 * The label is passed in already translated rather than projected: an `<ng-content>` that lives in
 * two branches of a control-flow block is only ever filled once by Angular, so switching between
 * dot and pill would silently blank the text.
 */
@Component({
  selector: 'app-health-marker',
  imports: [StateDot, StatusBadge],
  template: `
    @if (badgeTone(); as badge) {
      <app-status-badge [tone]="badge">{{ label() }}</app-status-badge>
    } @else {
      <app-state-dot [tone]="tone() === 'ok' ? 'on' : 'off'">{{ label() }}</app-state-dot>
    }
  `,
})
export class HealthMarker {
  readonly tone = input.required<HealthTone>();
  readonly label = input.required<string>();

  protected readonly badgeTone = computed(() => BADGE_TONES[this.tone()] ?? null);
}
