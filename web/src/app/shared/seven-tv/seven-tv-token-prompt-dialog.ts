import { DialogRef } from '@angular/cdk/dialog';
import { Component, effect, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { SevenTvTokenInput } from './seven-tv-token-input';

/** Opened via cdk Dialog when a delete run starts without a stored 7TV token. Closes itself with
 *  `true` the moment the token input stores one, so the caller can chain straight into the
 *  confirm dialog — same flow the old hand-built overlay had via its reactive template switch. */
@Component({
  selector: 'app-seven-tv-token-prompt-dialog',
  imports: [SevenTvTokenInput, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-slate-900 p-6 shadow-xl">
      <app-seven-tv-token-input />
      <button type="button" class="mt-4 text-sm text-slate-400 hover:underline" (click)="dialogRef.close(false)">
        {{ 'common.cancel' | transloco }}
      </button>
    </div>
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
