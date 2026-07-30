import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import { DeleteConfirmDialog, DeleteConfirmDialogData } from './delete-confirm-dialog';
import { DeleteProgressPanel } from './delete-progress-panel';
import { SevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

export interface DeletableEmote {
  emoteId: string;
  sevenTvEmoteId: string;
  name: string;
}

/**
 * One reusable delete engine, rendered as two separate instances (Usage-Stats page and
 * Voting-Results page) — each instance owns its host page's local selection, but both talk to
 * the same singleton SevenTvDeleteService/SevenTvTokenService underneath.
 */
@Component({
  selector: 'app-mass-delete-panel',
  imports: [Button, DeleteProgressPanel, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-3">
      <div class="flex flex-wrap items-center gap-2">
        <button
          type="button"
          appButton="danger-solid"
          buttonSize="lg"
          class="disabled:cursor-not-allowed"
          [disabled]="selectedEmotes().length === 0 || deleteService.isRunning()"
          (click)="openConfirm()"
        >
          {{ 'massDelete.deleteButton' | transloco: { count: selectedEmotes().length } }}
        </button>
        <!-- Without this, the only way out of a large selection was deselecting card by card
             (user finding, 2026-07-30) — the panel owns the "n selected" wording, so the way to
             zero belongs next to it; the actual clear stays with the host page's selection. -->
        @if (selectedEmotes().length > 0 && !deleteService.isRunning()) {
          <button
            type="button"
            appButton="neutral"
            buttonSize="lg"
            (click)="selectionCleared.emit()"
          >
            {{ 'massDelete.clearSelection' | transloco }}
          </button>
        }
      </div>

      @if (deleteService.isRunning() || deleteService.queue().length > 0) {
        <app-delete-progress-panel
          [items]="deleteService.queue()"
          [isRunning]="deleteService.isRunning()"
          [syncReport]="deleteService.syncReport()"
          (cancelled)="deleteService.cancel()"
          (syncRetryRequested)="deleteService.retrySyncReport()"
        />
      }
    </div>
  `,
})
export class MassDeletePanel {
  readonly setId = input.required<string>();
  readonly channelName = input.required<string>();
  readonly selectedEmotes = input.required<DeletableEmote[]>();

  /** Ids the host page may drop from its list without a refetch — emitted only once the backend has
   *  confirmed it archived them. */
  readonly deleted = output<string[]>();

  /** The run finished on 7TV, but the backend does not (fully) know about it. The host page must
   *  reload rather than filter locally, so it never shows a state the server does not share. */
  readonly reloadRequested = output<void>();

  /** The user wants the whole selection gone — the host page owns the ListSelection, so clearing
   *  is its job, not this panel's. */
  readonly selectionCleared = output<void>();

  protected readonly tokenService = inject(SevenTvTokenService);
  protected readonly deleteService = inject(SevenTvDeleteService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly dialog = inject(Dialog);

  private readonly setWarning = signal<EmoteSetWarning | null>(null);
  private readonly warningLoading = signal(false);
  private readonly selectedEmoteNames = computed(() =>
    this.selectedEmotes().map((emote) => emote.name),
  );

  constructor() {
    // The queue settling is not on its own a reason to tell the host page anything: the backend only
    // learns about the deletion through the closing sync-deleted call, and that call can fail (rate
    // limit, session expired mid-run). Emitting on the isRunning edge alone therefore showed a
    // cleaned-up list while the database still held every emote. So: wait for a terminal sync
    // report, then either allow the optimistic drop or ask for a real reload.
    let notifiedForThisRun = false;
    effect(() => {
      const running = this.deleteService.isRunning();
      const report = this.deleteService.syncReport();

      if (running) {
        notifiedForThisRun = false;
        return;
      }

      // 'idle' also covers a run in which nothing succeeded — there is nothing to report either way.
      if (notifiedForThisRun || report === 'idle' || report === 'pending') {
        return;
      }

      notifiedForThisRun = true;
      if (report !== 'succeeded') {
        this.reloadRequested.emit();
        return;
      }

      const doneIds = this.deleteService
        .queue()
        .filter((item) => item.status === 'done')
        .map((item) => item.emoteId);
      if (doneIds.length > 0) {
        this.deleted.emit(doneIds);
      }
    });
  }

  protected openConfirm(): void {
    // No stored 7TV token yet: ask for it first. The prompt closes itself with `true` the moment
    // the token is saved, which chains straight into the confirm dialog — the flow the old
    // hand-built overlay produced via its reactive template switch.
    if (!this.tokenService.hasToken()) {
      const promptRef = this.dialog.open<boolean>(SevenTvTokenPromptDialog, {
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
      });
      promptRef.closed.subscribe((saved) => {
        if (saved) {
          this.openConfirmDialog();
        }
      });
      return;
    }

    this.openConfirmDialog();
  }

  private openConfirmDialog(): void {
    this.setWarning.set(null);
    this.warningLoading.set(true);

    // The dialog is already open while this runs — it reads the panel's signals live (see
    // DeleteConfirmDialogData), so the shared-set warning pops in as soon as the check answers.
    this.loadSetWarning();

    const data: DeleteConfirmDialogData = {
      emotes: this.selectedEmoteNames,
      warning: this.setWarning.asReadonly(),
      warningLoading: this.warningLoading.asReadonly(),
    };
    const confirmRef = this.dialog.open<boolean>(DeleteConfirmDialog, {
      data,
      backdropClass: 'app-dialog-backdrop',
      panelClass: 'app-dialog-panel',
      ariaLabelledBy: 'delete-confirm-dialog-title',
    });
    confirmRef.closed.subscribe((confirmed) => {
      if (confirmed) {
        this.startDelete();
      }
    });
  }

  private loadSetWarning(): void {
    this.emoteAdminService.getSetWarning(this.channelName()).subscribe({
      next: (warning) => {
        this.setWarning.set(warning);
        this.warningLoading.set(false);
      },
      error: () => {
        // `available: false` is the signal the dialog acts on — it renders a neutral "couldn't
        // check" notice instead of the red "confirmed foreign set" alarm, so a failed check no
        // longer produces a false accusation. `isOwnSet` stays `false` deliberately: it is
        // meaningless while `available` is false, and should a future reader consume it without
        // checking `available`, the conservative direction ("not verified as ours") is the safe one.
        this.setWarning.set({
          available: false,
          isOwnSet: false,
          otherTrackedChannelsSharingSet: [],
          otherModeratedChannelsSharingSet: [],
        });
        this.warningLoading.set(false);
      },
    });
  }

  private startDelete(): void {
    const emotes: DeleteQueueEmote[] = this.selectedEmotes().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.name,
    }));
    this.deleteService.startDelete(this.setId(), this.channelName(), emotes);
  }
}
