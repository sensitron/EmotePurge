import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';

export type ExportFormat = 'csv' | 'json';

/** What gets exported: the visible (filtered + sorted) list, or the current grid selection. */
export type ExportScope = 'visible' | 'selection';

export interface ExportChoice {
  format: ExportFormat;
  scope: ExportScope;
}

export interface ExportDialogData {
  /** Rows of the visible list — the default export, so the count is part of the confirmation. */
  rowCount: number;
  /** True when the visible list is a filtered subset; renders the "filtered" hint line. */
  filtered: boolean;
  /**
   * Size of the page's grid selection; 0 means no selection exists and the scope choice is not
   * offered at all — a zero-row export is never a valid answer.
   */
  selectionCount: number;
  /**
   * Pre-resolved Transloco keys explaining *why* columns will be missing (secret ballot,
   * manager-only usage). This dialog is the one place that explanation can live — the file itself
   * cannot carry it.
   */
  noticeKeys: readonly string[];
}

/** Closes with the chosen format + scope, or `undefined` on cancel/Escape/backdrop. */
@Component({
  selector: 'app-export-dialog',
  imports: [Button, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-surface p-6 shadow-overlay">
      <h2 id="export-dialog-title" class="mb-1 text-lg font-semibold">
        {{ 'export.title' | transloco }}
      </h2>
      @if (data.selectionCount > 0) {
        <div
          class="mb-1 flex flex-wrap gap-4 text-sm text-fg-secondary"
          role="radiogroup"
          [attr.aria-label]="'export.scopeLabel' | transloco"
        >
          <label class="flex items-center gap-2 py-1">
            <input
              type="radio"
              class="h-4 w-4 accent-accent-solid"
              name="export-scope"
              [checked]="scope() === 'visible'"
              (change)="scope.set('visible')"
            />
            {{ 'export.scopeVisible' | transloco: { count: data.rowCount } }}
          </label>
          <label class="flex items-center gap-2 py-1">
            <input
              type="radio"
              class="h-4 w-4 accent-accent-solid"
              name="export-scope"
              [checked]="scope() === 'selection'"
              (change)="scope.set('selection')"
            />
            {{ 'export.scopeSelection' | transloco: { count: data.selectionCount } }}
          </label>
        </div>
      }
      <p class="mb-1 text-sm text-fg-secondary">
        {{ rowCountKey() | transloco: { count: exportRowCount() } }}
      </p>
      @if (data.filtered && scope() === 'visible') {
        <p class="mb-1 text-xs text-fg-muted">{{ 'export.filteredHint' | transloco }}</p>
      }
      @for (noticeKey of data.noticeKeys; track noticeKey) {
        <p class="mt-2 text-sm text-warning-fg" role="status">{{ noticeKey | transloco }}</p>
      }
      <div class="mt-4 flex flex-wrap justify-end gap-2">
        <button type="button" appButton="outline" buttonSize="lg" (click)="dialogRef.close()">
          {{ 'common.cancel' | transloco }}
        </button>
        <button type="button" appButton="neutral" buttonSize="lg" (click)="close('json')">
          {{ 'export.formatJson' | transloco }}
        </button>
        <button type="button" appButton="primary" buttonSize="lg" (click)="close('csv')">
          {{ 'export.formatCsv' | transloco }}
        </button>
      </div>
    </div>
  `,
})
export class ExportDialog {
  protected readonly data = inject<ExportDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ExportChoice | undefined>>(DialogRef);

  // Defaults to the visible list even when a selection exists: the selection also drives
  // mass-delete and vote-session creation, and an export must never silently narrow to it.
  protected readonly scope = signal<ExportScope>('visible');

  protected readonly exportRowCount = computed(() =>
    this.scope() === 'selection' ? this.data.selectionCount : this.data.rowCount,
  );

  protected readonly rowCountKey = computed(() =>
    pluralKey(this.exportRowCount(), 'export.rowCount'),
  );

  protected close(format: ExportFormat): void {
    this.dialogRef.close({ format, scope: this.scope() });
  }
}
