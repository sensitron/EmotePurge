import { Dialog } from '@angular/cdk/dialog';
import { Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { DeleteQueueEmote } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow, parsePurgeRunProtocol } from '../export/purge-run-export';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/**
 * The half of the A6 safety net that survives a reload: re-import of a downloaded purge protocol.
 * The post-run restore button covers the immediate "wrong selection, panel still open" panic; this
 * panel makes the file itself the restore list. Validation happens in parsePurgeRunProtocol —
 * channel *and* active set id must match, and only rows the delete actually removed are offered.
 * The restore run itself renders in the mass-delete panel's restore progress section.
 */
@Component({
  selector: 'app-restore-panel',
  imports: [Button, NoticeBanner, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-2">
      <div class="flex flex-wrap items-center gap-2">
        <button
          type="button"
          appButton="outline"
          [disabled]="restoreService.isRunning()"
          class="disabled:cursor-not-allowed"
          (click)="openFilePicker()"
        >
          {{ 'restore.import.trigger' | transloco }}
        </button>
        <span class="text-xs text-fg-muted">{{ 'restore.import.hint' | transloco }}</span>
        <input
          #fileInput
          type="file"
          accept="application/json"
          class="hidden"
          [attr.aria-label]="'restore.import.fileLabel' | transloco"
          (change)="onFileSelected($event)"
        />
      </div>
      @if (errorKey(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }
    </div>
  `,
})
export class RestorePanel {
  readonly channelName = input.required<string>();
  /** The channel's *current* active set — the protocol is validated against it. */
  readonly setId = input.required<string>();

  protected readonly restoreService = inject(SevenTvRestoreService);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly dialog = inject(Dialog);

  // Named apart from the #fileInput template reference: inside the template the bare name
  // resolves to the reference (the raw element), which is not callable — AOT rejects it.
  private readonly fileInputRef = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');
  protected readonly errorKey = signal<string | null>(null);

  private readonly restoreSlots = signal<{ occupied: number; capacity: number } | null>(null);

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
    const parsed = parsePurgeRunProtocol(await file.text(), {
      channelName: this.channelName(),
      emoteSetId: this.setId(),
    });
    if (!parsed.ok) {
      this.errorKey.set(parsed.errorKey);
      return;
    }

    if (!this.tokenService.hasToken()) {
      openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
        if (saved) {
          this.openConfirm(parsed.rows);
        }
      });
      return;
    }
    this.openConfirm(parsed.rows);
  }

  private openConfirm(rows: PurgeRunRow[]): void {
    // Live slot view, same pattern as the delete confirm's shared-set warning.
    this.restoreSlots.set(null);
    this.emoteAdminService.getSetStatus(this.channelName()).subscribe({
      next: (status) =>
        this.restoreSlots.set(
          status.capacity === null
            ? null
            : { occupied: status.occupiedSlots, capacity: status.capacity },
        ),
      error: () => this.restoreSlots.set(null),
    });

    const data: RestoreConfirmDialogData = {
      names: rows.map((row) => row.name),
      slots: this.restoreSlots.asReadonly(),
    };
    openRestoreConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (confirmed) {
        const emotes: DeleteQueueEmote[] = rows.map((row) => ({
          emoteId: row.emoteId,
          sevenTvEmoteId: row.sevenTvEmoteId,
          name: row.name,
        }));
        this.restoreService.startRestore(this.setId(), this.channelName(), emotes);
      }
    });
  }
}
