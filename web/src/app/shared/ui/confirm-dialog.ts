import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from './button';

/** Caller passes already-translated strings — both call sites interpolate params (channel name,
 *  session title) via TranslocoService anyway. */
export interface ConfirmDialogData {
  message: string;
  confirmLabel: string;
}

/** Replaces window.confirm for destructive actions (leave channel, delete session): same look as
 *  the other cdk dialogs, Escape/backdrop cancel, closes with `true` only on explicit confirm. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [Button, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-slate-900 p-6 shadow-xl">
      <p class="mb-5 text-sm whitespace-pre-line text-slate-200">{{ data.message }}</p>
      <div class="flex justify-end gap-2">
        <button type="button" appButton="outline" buttonSize="lg" (click)="dialogRef.close(false)">
          {{ 'common.cancel' | transloco }}
        </button>
        <button
          type="button"
          appButton="danger-solid"
          buttonSize="lg"
          (click)="dialogRef.close(true)"
        >
          {{ data.confirmLabel }}
        </button>
      </div>
    </div>
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
}
