import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * Hierarchical "up" link — always points at the destination's own place in the information
 * hierarchy, never at `history.back()`. Every deep page here is regularly entered by deep link
 * (a fresh session opens from usage-stats, from My-Votings, from the admin area), so the browser's
 * previous page is routinely *not* the parent; a history-back affordance would send people
 * somewhere they never were. See design doc §8.6.
 *
 * `label` arrives already translated, like every other primitive in this folder. The accessible
 * name is widened to "Back to <label>" so the link announces its function and not just a bare
 * destination noun — that wording is the primitive's business, not the caller's, hence the one
 * transloco lookup inside. WCAG 2.5.3 holds: the visible text is contained in the accessible name.
 */
@Component({
  selector: 'app-back-link',
  imports: [RouterLink, TranslocoPipe],
  template: `
    <a
      [routerLink]="link()"
      [attr.aria-label]="'nav.backTo' | transloco: { target: label() }"
      class="inline-flex items-center gap-1 rounded-md border border-accent-selected px-3 py-1.5 text-sm whitespace-nowrap text-accent-fg transition hover:bg-accent-wash"
    >
      <span aria-hidden="true">←</span>{{ label() }}
    </a>
  `,
})
export class BackLink {
  /** Router link commands for the PARENT page — a string for a fixed path, an array when it is
   *  built from route params (e.g. `['/channels', channelName(), 'vote-sessions']`). */
  readonly link = input.required<string | unknown[]>();
  readonly label = input.required<string>();
}
