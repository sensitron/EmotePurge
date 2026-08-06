import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, effect, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { SevenTvTokenInput } from './seven-tv-token-input';

/** Opened via cdk Dialog when a delete run starts without a stored 7TV token. Closes itself with
 *  `true` the moment the token input stores one, so the caller can chain straight into the
 *  confirm dialog — same flow the old hand-built overlay had via its reactive template switch. */
@Component({
  selector: 'app-seven-tv-token-prompt-dialog',
  imports: [Button, DialogShell, SevenTvTokenInput, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'sevenTvToken.dialogTitle' | transloco">
      <app-seven-tv-token-input />

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close(false)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class SevenTvTokenPromptDialog {
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
  private readonly tokenService = inject(SevenTvTokenService);

  constructor() {
    effect(() => {
      if (this.tokenService.hasToken()) {
        this.dialogRef.close(true);
      }
    });
  }
}

export function openSevenTvTokenPromptDialog(dialog: Dialog): DialogRef<boolean> {
  return openAppDialog<boolean>(dialog, SevenTvTokenPromptDialog);
}
