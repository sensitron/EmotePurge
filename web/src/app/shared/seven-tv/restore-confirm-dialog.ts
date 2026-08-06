import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NamePreviewList } from '../ui/name-preview-list';
import { NoticeBanner } from '../ui/notice-banner';

export interface RestoreConfirmDialogData {
  /** Names of the emotes about to be re-added — the preview list, capped like the delete's. */
  names: readonly string[];
  /** Live view of the set status, so the capacity line pops in once the check answers.
   *  null = unknown (no capacity reported) — then no projection line is shown at all. */
  slots: Signal<{ occupied: number; capacity: number } | null>;
}

/**
 * The restore counterpart of DeleteConfirmDialog. Same two-tier *shape* as the destructive
 * convention (outline trigger → solid executor), but deliberately not its *colour*: restoring is
 * constructive, `danger` would be a false statement — the executor is `primary`.
 * Warns (never blocks) when the set would overflow: 7TV is the authority on its own capacity.
 */
@Component({
  selector: 'app-restore-confirm-dialog',
  imports: [Button, DialogShell, NamePreviewList, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey | transloco: { count: data.names.length }">
      <app-name-preview-list [names]="data.names" />

      @if (projection(); as slots) {
        @if (slots.overflow) {
          <app-notice-banner variant="warning">
            <span class="flex flex-col gap-1">
              <span>
                {{
                  'restore.capacityProjection'
                    | transloco: { projected: slots.projected, capacity: slots.capacity }
                }}
              </span>
              <span>{{ 'restore.capacityWarning' | transloco }}</span>
            </span>
          </app-notice-banner>
        } @else {
          <p class="text-sm text-fg-muted">
            {{
              'restore.capacityProjection'
                | transloco: { projected: slots.projected, capacity: slots.capacity }
            }}
          </p>
        }
      }
      <p class="text-xs text-fg-muted">{{ 'restore.historyNote' | transloco }}</p>

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
        appButton="primary"
        buttonSize="lg"
        (click)="dialogRef.close(true)"
      >
        {{ 'restore.confirmExecute' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class RestoreConfirmDialog {
  protected readonly data = inject<RestoreConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);

  protected readonly titleKey = pluralKey(this.data.names.length, 'restore.confirmTitle');

  protected readonly projection = computed(() => {
    const slots = this.data.slots();
    if (!slots || slots.capacity <= 0) {
      return null;
    }
    const projected = slots.occupied + this.data.names.length;
    return { projected, capacity: slots.capacity, overflow: projected > slots.capacity };
  });
}

export function openRestoreConfirmDialog(
  dialog: Dialog,
  data: RestoreConfirmDialogData,
): DialogRef<boolean> {
  return openAppDialog<boolean, RestoreConfirmDialogData>(dialog, RestoreConfirmDialog, { data });
}
