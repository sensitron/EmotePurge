import { Component, computed, inject, input } from '@angular/core';

import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { DIALOG_TITLE_ID } from './dialog';
import { SheetDrag } from './sheet-drag';

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
 *
 * On a coarse pointer the same dialog is a bottom sheet: the pane's geometry comes from a media
 * query in styles.css, and what is added here is the chrome that only a sheet has — a grab handle
 * and the drag that dismisses it. Sticky, because the pane is the scroll container, and a handle
 * that scrolls out of reach is not a handle.
 *
 * The handle carries the hull's own `rounded-t-2xl`, because its negative margins bleed it into the
 * corner areas the hull's radius leaves unpainted — without it the sheet reads square-topped and the
 * hull's radius is invisible. `overflow-hidden` on the hull would fix it too and must not be used:
 * it would make the hull a scroll container of its own and break the handle's sticky pin.
 */
@Component({
  selector: 'app-dialog-shell',
  imports: [SheetDrag],
  template: `
    <div [class]="hullClasses()" [appSheetDrag]="isSheet()">
      @if (isSheet()) {
        <div
          data-sheet-handle
          class="sticky -top-6 -mx-6 -mt-6 flex touch-none justify-center rounded-t-2xl bg-surface pt-2 pb-3"
          aria-hidden="true"
        >
          <span class="h-1 w-9 rounded-full bg-border-strong"></span>
        </div>
      }

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

  private readonly pointerMode = inject(PointerModeService);

  protected readonly isSheet = this.pointerMode.isCoarse;

  protected readonly hullClasses = computed(
    () =>
      'flex flex-col gap-4 bg-surface p-6 shadow-overlay ' +
      (this.isSheet() ? 'rounded-t-2xl' : 'rounded-lg'),
  );

  protected readonly titleId = DIALOG_TITLE_ID;
}
