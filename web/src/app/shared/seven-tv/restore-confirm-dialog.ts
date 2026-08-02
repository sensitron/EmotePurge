import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';

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
  imports: [Button, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-surface p-6 shadow-overlay">
      <h2 id="restore-confirm-dialog-title" class="mb-3 text-lg font-semibold">
        {{ titleKey | transloco: { count: data.names.length } }}
      </h2>
      <ul class="mb-4 max-h-48 space-y-1 overflow-y-auto text-sm text-fg-secondary">
        @for (name of previewNames(); track name) {
          <li>{{ name }}</li>
        }
        @if (data.names.length > previewNames().length) {
          <li class="text-fg-muted">
            {{ andMoreKey() | transloco: { count: data.names.length - previewNames().length } }}
          </li>
        }
      </ul>

      @if (projection(); as slots) {
        <p class="mb-1 text-sm" [class]="slots.overflow ? 'text-warning-fg' : 'text-fg-muted'">
          {{
            'restore.capacityProjection'
              | transloco: { projected: slots.projected, capacity: slots.capacity }
          }}
        </p>
        @if (slots.overflow) {
          <p class="mb-1 text-sm text-warning-fg" role="alert">
            {{ 'restore.capacityWarning' | transloco }}
          </p>
        }
      }
      <p class="mb-4 text-xs text-fg-muted">{{ 'restore.historyNote' | transloco }}</p>

      <div class="flex justify-end gap-2">
        <button type="button" appButton="outline" buttonSize="lg" (click)="dialogRef.close(false)">
          {{ 'common.cancel' | transloco }}
        </button>
        <button type="button" appButton="primary" buttonSize="lg" (click)="dialogRef.close(true)">
          {{ 'restore.confirmExecute' | transloco }}
        </button>
      </div>
    </div>
  `,
})
export class RestoreConfirmDialog {
  protected readonly data = inject<RestoreConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);

  protected readonly titleKey = pluralKey(this.data.names.length, 'restore.confirmTitle');
  protected readonly previewNames = computed(() => this.data.names.slice(0, 50));
  protected readonly andMoreKey = computed(() =>
    pluralKey(this.data.names.length - this.previewNames().length, 'massDelete.andMore'),
  );

  protected readonly projection = computed(() => {
    const slots = this.data.slots();
    if (!slots || slots.capacity <= 0) {
      return null;
    }
    const projected = slots.occupied + this.data.names.length;
    return { projected, capacity: slots.capacity, overflow: projected > slots.capacity };
  });
}
