import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';

export type ExportFormat = 'csv' | 'json';

export interface ExportDialogData {
  /** Rows the export will contain — the visible list, so the count is part of the confirmation. */
  rowCount: number;
  /** True when the visible list is a filtered subset; renders the "filtered" hint line. */
  filtered: boolean;
  /**
   * Pre-resolved Transloco keys explaining *why* columns will be missing (secret ballot,
   * manager-only usage). This dialog is the one place that explanation can live — the file itself
   * cannot carry it.
   */
  noticeKeys: readonly string[];
}

/** Closes with the chosen format, or `undefined` on cancel/Escape/backdrop. */
@Component({
  selector: 'app-export-dialog',
  imports: [Button, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-surface p-6 shadow-overlay">
      <h2 id="export-dialog-title" class="mb-1 text-lg font-semibold">
        {{ 'export.title' | transloco }}
      </h2>
      <p class="mb-1 text-sm text-fg-secondary">
        {{ rowCountKey | transloco: { count: data.rowCount } }}
      </p>
      @if (data.filtered) {
        <p class="mb-1 text-xs text-fg-muted">{{ 'export.filteredHint' | transloco }}</p>
      }
      @for (noticeKey of data.noticeKeys; track noticeKey) {
        <p class="mt-2 text-sm text-warning-fg" role="status">{{ noticeKey | transloco }}</p>
      }
      <div class="mt-4 flex flex-wrap justify-end gap-2">
        <button type="button" appButton="outline" buttonSize="lg" (click)="dialogRef.close()">
          {{ 'common.cancel' | transloco }}
        </button>
        <button type="button" appButton="neutral" buttonSize="lg" (click)="dialogRef.close('json')">
          {{ 'export.formatJson' | transloco }}
        </button>
        <button type="button" appButton="primary" buttonSize="lg" (click)="dialogRef.close('csv')">
          {{ 'export.formatCsv' | transloco }}
        </button>
      </div>
    </div>
  `,
})
export class ExportDialog {
  protected readonly data = inject<ExportDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ExportFormat | undefined>>(DialogRef);

  protected readonly rowCountKey = pluralKey(this.data.rowCount, 'export.rowCount');
}
