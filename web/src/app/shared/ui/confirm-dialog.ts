import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from './button';
import { openAppDialog } from './dialog';
import { DialogShell } from './dialog-shell';

/** Caller passes already-translated strings — both call sites interpolate params (channel name,
 *  session title) via TranslocoService anyway. */
export interface ConfirmDialogData {
  message: string;
  confirmLabel: string;
}

/** Replaces window.confirm for destructive actions (leave channel, delete session): Escape/backdrop
 *  cancel, closes with `true` only on explicit confirm. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [Button, DialogShell, TranslocoPipe],
  template: `
    <app-dialog-shell>
      <p class="text-sm whitespace-pre-line text-fg-body">{{ data.message }}</p>

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close(false)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="danger-solid"
        buttonSize="lg"
        (click)="dialogRef.close(true)"
      >
        {{ data.confirmLabel }}
      </button>
    </app-dialog-shell>
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
}

/**
 * Deliberately headingless: every message this dialog carries already opens by restating the action
 * ("Channel #x wirklich verlassen?", "Abstimmung „x" beenden?"), so an `<h2>` above it would be the
 * same sentence twice. The short action phrase names the dialog for assistive tech instead — before
 * this, the dialog had no accessible name at all.
 */
export function openConfirmDialog(dialog: Dialog, data: ConfirmDialogData): DialogRef<boolean> {
  return openAppDialog<boolean, ConfirmDialogData>(dialog, ConfirmDialog, {
    data,
    ariaLabel: data.confirmLabel,
  });
}
