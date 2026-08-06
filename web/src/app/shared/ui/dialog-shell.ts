import { Component, input } from '@angular/core';

import { DIALOG_TITLE_ID } from './dialog';

/**
 * The inside of every CDK dialog: surface, padding, heading, body, action row.
 *
 * The hull (`rounded-lg bg-surface p-6 shadow-overlay`) stood hand-written in nine components, and
 * around it the details had drifted the way copies do — five heading variants, three heading
 * margins, two of the nine with no heading and therefore no accessible name at all.
 *
 * Spacing is the shell's job, not the caller's: the body is a flex column, so a dialog composes its
 * content out of siblings instead of stacking `mb-*` on each one. Content that belongs together
 * more tightly than the default rhythm wraps itself in its own `flex flex-col gap-1`.
 *
 * The width comes from `.cdk-overlay-pane.app-dialog-panel` (styles.css) — the pane owns it, not the
 * content. Three dialogs used to also set their own `w-[26rem]`/`w-[28rem]`, which either matched
 * the pane cap or silently fought it.
 */
@Component({
  selector: 'app-dialog-shell',
  template: `
    <div class="flex flex-col gap-4 rounded-lg bg-surface p-6 shadow-overlay">
      <!-- For a heading that is more than text (the drilldown's emote thumbnail). The projected
           markup carries id="app-dialog-title" on its own h2 — see DIALOG_TITLE_ID. -->
      <ng-content select="[dialog-header]" />
      @if (dialogTitle(); as title) {
        <h2 [id]="titleId" class="text-lg font-semibold text-balance text-fg">{{ title }}</h2>
      }

      <div class="flex flex-col gap-3"><ng-content /></div>

      <!-- Cancel goes first, always: the CDK's first-tabbable autoFocus default then lands on the
           harmless control, which is what makes an explicit cdkFocusInitial unnecessary. -->
      <div class="flex flex-wrap items-center justify-end gap-2">
        <ng-content select="[dialog-actions]" />
      </div>
    </div>
  `,
})
export class DialogShell {
  /** Already translated. Omitted only when a `[dialog-header]` renders the heading instead. */
  readonly dialogTitle = input<string>();

  protected readonly titleId = DIALOG_TITLE_ID;
}
