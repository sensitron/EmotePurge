import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';

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

/**
 * Closes with the chosen format + scope, or `undefined` on cancel/Escape/backdrop.
 *
 * The format used to be two footer buttons next to Cancel, which made a *choice* look like two
 * competing exits and forced one of the two equal formats into the quieter variant. It is a radio
 * group now, matching the scope choice directly above it, and the footer states one action.
 */
@Component({
  selector: 'app-export-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'export.title' | transloco">
      @if (data.selectionCount > 0) {
        <div
          class="flex flex-wrap gap-4 text-sm text-fg-secondary"
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

      <div
        class="flex flex-wrap gap-4 text-sm text-fg-secondary"
        role="radiogroup"
        [attr.aria-label]="'export.formatLabel' | transloco"
      >
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-accent-solid"
            name="export-format"
            [checked]="format() === 'csv'"
            (change)="format.set('csv')"
          />
          {{ 'export.formatCsv' | transloco }}
        </label>
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-accent-solid"
            name="export-format"
            [checked]="format() === 'json'"
            (change)="format.set('json')"
          />
          {{ 'export.formatJson' | transloco }}
        </label>
      </div>

      <div class="flex flex-col gap-1">
        <p class="text-sm text-fg-secondary">
          {{ rowCountKey() | transloco: { count: exportRowCount() } }}
        </p>
        @if (data.filtered && scope() === 'visible') {
          <p class="text-xs text-fg-muted">{{ 'export.filteredHint' | transloco }}</p>
        }
      </div>

      @for (noticeKey of data.noticeKeys; track noticeKey) {
        <app-notice-banner variant="info">{{ noticeKey | transloco }}</app-notice-banner>
      }

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close()"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button dialog-actions type="button" appButton="primary" buttonSize="lg" (click)="submit()">
        {{ 'export.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class ExportDialog {
  protected readonly data = inject<ExportDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ExportChoice | undefined>>(DialogRef);

  // Defaults to the visible list even when a selection exists: the selection also drives
  // mass-delete and vote-session creation, and an export must never silently narrow to it.
  protected readonly scope = signal<ExportScope>('visible');
  // CSV first — it is what the spreadsheet the mods actually use opens; JSON is the escape hatch.
  protected readonly format = signal<ExportFormat>('csv');

  protected readonly exportRowCount = computed(() =>
    this.scope() === 'selection' ? this.data.selectionCount : this.data.rowCount,
  );

  protected readonly rowCountKey = computed(() =>
    pluralKey(this.exportRowCount(), 'export.rowCount'),
  );

  protected submit(): void {
    this.dialogRef.close({ format: this.format(), scope: this.scope() });
  }
}

export function openExportDialog(
  dialog: Dialog,
  data: ExportDialogData,
): DialogRef<ExportChoice | undefined> {
  return openAppDialog<ExportChoice | undefined, ExportDialogData>(dialog, ExportDialog, { data });
}
