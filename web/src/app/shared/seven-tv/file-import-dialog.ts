import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ImportSource } from '../../core/seven-tv/import-source';
import { parseImportSource } from '../export/import-source-parser';
import { PurgeRunRow, parsePurgeRunProtocol } from '../export/purge-run-export';
import { readEnvelope } from '../export/read-envelope';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';

/**
 * Frozen at the moment of the triggering click (#91) — never a live signal, so a channel switch or
 * a set change while this dialog is open cannot retarget what a purge-run protocol is validated
 * against. Both feed `parsePurgeRunProtocol` (`purge-run-export.ts`) unchanged.
 */
export interface FileImportDialogData {
  channelName: string;
  setId: string;
}

/**
 * What the dialog closes with on success. The discriminant tells the caller which chain to run
 * next — `startRestoreFlow` for `'restore'`, `startImportFlow` for `'import'` — this dialog starts
 * neither itself and picks no import target; that stays the caller's business. `undefined` on
 * cancel, Escape or a backdrop click, same as every other dialog in the app.
 */
export type FileImportResult =
  { kind: 'restore'; rows: PurgeRunRow[] } | { kind: 'import'; source: ImportSource };

/**
 * The read-and-validate step of the file-based restore/import path (#91). This dialog only reads
 * the file and reports what it is; it never starts a run and never opens a further dialog — that
 * is `FileImportTrigger`'s job. Keeping both chains outside of this dialog
 * (Restore: token → confirm; Import: confirm → token, both unchanged) means they never nest inside
 * an already-open dialog, preserving the app's one-dialog-at-a-time contract (`shared/ui/dialog.ts`).
 *
 * Body order is a contract (plan §1.1, later documented as design-language §7.3): heading, the
 * three acceptable file sorts — so the explanation sits *above* the control it explains — the file
 * control, the error banner (only on failure), then cancel-only actions; there is no "weiter" step,
 * the file pick itself is the action. On failure the dialog stays open and the banner replaces
 * itself on every new attempt; on success the dialog closes with the result above.
 *
 * The file control is deliberately the first focusable element in the dialog — not
 * `cdkFocusInitial` — so the CDK's `first-tabbable` default lands there on its own
 * (`dialog-shell.ts`: the body precedes the action row). A hidden `<input type="file">` cannot be
 * that element itself, so the visible button in front of it is.
 */
@Component({
  selector: 'app-file-import-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'restore.import.title' | transloco">
      <ul class="list-disc space-y-1 pl-5 text-sm text-fg-secondary">
        <li>{{ 'restore.import.sorts.purgeRun' | transloco }}</li>
        <li>{{ 'restore.import.sorts.emoteList' | transloco }}</li>
        <li>{{ 'restore.import.sorts.usageExport' | transloco }}</li>
      </ul>

      <div>
        <button type="button" appButton="outline" (click)="openFilePicker()">
          {{ 'restore.import.fileLabel' | transloco }}
        </button>
        <input
          #fileInput
          type="file"
          accept="application/json"
          class="hidden"
          (change)="onFileSelected($event)"
        />
      </div>

      @if (errorKey(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
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
    </app-dialog-shell>
  `,
})
export class FileImportDialog {
  private readonly data = inject<FileImportDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<FileImportResult | undefined>>(DialogRef);

  // Named apart from the #fileInput template reference, same reasoning as the panel this was split
  // out of: inside the template the bare name resolves to the reference (the raw element), which is
  // not callable — AOT rejects it.
  private readonly fileInputRef = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');
  protected readonly errorKey = signal<string | null>(null);

  protected openFilePicker(): void {
    this.fileInputRef().nativeElement.click();
  }

  protected async onFileSelected(event: Event): Promise<void> {
    const inputElement = event.target as HTMLInputElement;
    const file = inputElement.files?.[0];
    // Clear the input either way, so re-selecting the same (corrected) file fires change again.
    inputElement.value = '';
    if (!file) {
      return;
    }

    this.errorKey.set(null);
    const text = await file.text();
    const read = readEnvelope(text);
    if (!read.ok) {
      this.errorKey.set(read.errorKey);
      return;
    }

    if (read.envelope.kind === 'purge-run') {
      // parsePurgeRunProtocol deliberately re-reads the very same text: it does its own envelope
      // check and keeps enforcing the channel *and* the active-set match (`wrongChannel`/`wrongSet`)
      // itself, which readEnvelope knows nothing about. The second pass is the contract, not a slip.
      this.handlePurgeRunProtocol(text);
      return;
    }

    const parsedSource = parseImportSource(read.envelope, file.name);
    if (!parsedSource.ok) {
      this.errorKey.set(parsedSource.errorKey);
      return;
    }
    this.dialogRef.close({ kind: 'import', source: parsedSource.source });
  }

  private handlePurgeRunProtocol(text: string): void {
    const parsed = parsePurgeRunProtocol(text, {
      channelName: this.data.channelName,
      emoteSetId: this.data.setId,
    });
    if (!parsed.ok) {
      this.errorKey.set(parsed.errorKey);
      return;
    }
    this.dialogRef.close({ kind: 'restore', rows: parsed.rows });
  }
}

export function openFileImportDialog(
  dialog: Dialog,
  data: FileImportDialogData,
): DialogRef<FileImportResult | undefined> {
  return openAppDialog<FileImportResult | undefined, FileImportDialogData>(
    dialog,
    FileImportDialog,
    { data },
  );
}
