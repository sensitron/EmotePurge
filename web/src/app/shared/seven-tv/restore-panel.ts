import { Dialog } from '@angular/cdk/dialog';
import { Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueEmote } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow, parsePurgeRunProtocol } from '../export/purge-run-export';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { RestoreConfirmDialog, RestoreConfirmDialogData } from './restore-confirm-dialog';
import { SevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

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
          (click)="fileInput().nativeElement.click()"
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

  protected readonly fileInput = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');
  protected readonly errorKey = signal<string | null>(null);

  private readonly restoreSlots = signal<{ occupied: number; capacity: number } | null>(null);

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
      const promptRef = this.dialog.open<boolean>(SevenTvTokenPromptDialog, {
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
      });
      promptRef.closed.subscribe((saved) => {
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
    this.dialog
      .open<boolean>(RestoreConfirmDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
        ariaLabelledBy: 'restore-confirm-dialog-title',
      })
      .closed.subscribe((confirmed) => {
        if (confirmed) {
          const emotes: RunQueueEmote[] = rows.map((row) => ({
            emoteId: row.emoteId,
            sevenTvEmoteId: row.sevenTvEmoteId,
            name: row.name,
          }));
          this.restoreService.startRestore(this.setId(), this.channelName(), emotes);
        }
      });
  }
}
