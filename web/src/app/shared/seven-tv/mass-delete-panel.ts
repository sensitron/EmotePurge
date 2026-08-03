import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { CSV_MIME } from '../export/csv';
import { ExportChoice, ExportDialog, ExportDialogData } from '../export/export-dialog';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import {
  buildPurgeRunProtocol,
  purgeRunCsv,
  purgeRunFilename,
  purgeRunJson,
} from '../export/purge-run-export';
import { Button } from '../ui/button';
import { DeleteConfirmDialog, DeleteConfirmDialogData } from './delete-confirm-dialog';
import { RestoreConfirmDialog, RestoreConfirmDialogData } from './restore-confirm-dialog';
import { RunProgressPanel } from './run-progress-panel';
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
 *
 * Since A6 it also owns the run's paper trail: the post-run summary offers the protocol as a
 * download (the file is the restore list), and "restore" re-adds the deleted emotes over the
 * restore service's own engine run — both browser-side, the 7TV token never leaves it.
 */
@Component({
  selector: 'app-mass-delete-panel',
  imports: [Button, RunProgressPanel, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-3">
      <div class="flex flex-wrap items-center gap-2">
        <!-- Host-page peers acting on the same grid selection (e.g. the usage page's
             create-vote-session button) share this row, constructive before destructive —
             stacked rows hid that both consume one selection and cost a row of vertical space. -->
        <ng-content select="[selection-actions]" />
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
        <app-run-progress-panel
          [items]="deleteService.queue()"
          [isRunning]="deleteService.isRunning()"
          [syncReport]="deleteService.syncReport()"
          [rateLimitPauseSeconds]="deleteService.rateLimitPauseSeconds()"
          (cancelled)="deleteService.cancel()"
          (dismissed)="deleteService.reset()"
          (syncRetryRequested)="deleteService.retrySyncReport()"
        >
          <ng-container run-actions>
            @if (deleteService.lastRun(); as run) {
              <button type="button" appButton="neutral" (click)="openProtocolExport()">
                {{ 'massDelete.summary.downloadProtocol' | transloco }}
              </button>
              @if (run.result.doneIds.length > 0 && !restoreService.isRunning()) {
                <!-- The two-tier *shape* of the destructive convention, not its colour: outline
                     triggers, the dialog's primary-solid executes — restore is constructive. -->
                <button type="button" appButton="outline" (click)="openRestoreConfirm()">
                  {{ 'restore.button' | transloco }}
                </button>
              }
              @if (!protocolSaved()) {
                <!-- reset() wipes the run — the downloaded file is the only durable artifact. -->
                <span class="text-xs text-fg-muted">
                  {{ 'massDelete.summary.protocolNotSaved' | transloco }}
                </span>
              }
            }
          </ng-container>
        </app-run-progress-panel>
      }

      @if (restoreService.isRunning() || restoreService.queue().length > 0) {
        <app-run-progress-panel
          [items]="restoreService.queue()"
          [isRunning]="restoreService.isRunning()"
          labelPrefix="restore"
          [syncReport]="restoreService.syncReport()"
          [rateLimitPauseSeconds]="restoreService.rateLimitPauseSeconds()"
          (cancelled)="restoreService.cancel()"
          (dismissed)="restoreService.reset()"
          (syncRetryRequested)="restoreService.retrySyncReport()"
        >
          <ng-container run-actions>
            @if (resyncNoticeKey(); as noticeKey) {
              <span class="text-xs text-fg-muted" role="status">{{ noticeKey | transloco }}</span>
            }
          </ng-container>
        </app-run-progress-panel>
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
  protected readonly restoreService = inject(SevenTvRestoreService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly dialog = inject(Dialog);

  private readonly setWarning = signal<EmoteSetWarning | null>(null);
  private readonly warningLoading = signal(false);
  private readonly selectedEmoteNames = computed(() =>
    this.selectedEmotes().map((emote) => emote.name),
  );

  /** Whether the current run's protocol was downloaded at least once — drives the reminder next
   *  to Close, since reset() leaves the file as the only artifact. */
  protected readonly protocolSaved = signal(false);

  /** Live slot view for the restore-confirm dialog, loaded when that dialog opens. */
  private readonly restoreSlots = signal<{ occupied: number; capacity: number } | null>(null);

  protected readonly resyncNoticeKey = computed(() => {
    switch (this.restoreService.resyncTrigger()) {
      case 'pending':
        return 'restore.resync.pending';
      case 'succeeded':
        return 'restore.resync.succeeded';
      case 'cooldown':
        return 'restore.resync.cooldown';
      case 'failed':
        return 'restore.resync.failed';
      default:
        return null;
    }
  });

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
        this.protocolSaved.set(false);
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

    // A finished restore changes the emote inventory back — the host page must refetch rather
    // than trust its current list, same reasoning as the delete's reload path.
    let restoreNotified = false;
    effect(() => {
      if (this.restoreService.isRunning()) {
        restoreNotified = false;
        return;
      }
      if (restoreNotified || this.restoreService.queue().length === 0) {
        return;
      }
      restoreNotified = true;
      this.reloadRequested.emit();
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

  /** Offers the finished run's protocol in both formats — the JSON is the restore list. */
  protected openProtocolExport(): void {
    const run = this.deleteService.lastRun();
    if (!run) {
      return;
    }
    const protocol = buildPurgeRunProtocol({
      channelName: run.channelName,
      emoteSetId: run.setId,
      startedAt: run.result.startedAt,
      finishedAt: run.result.finishedAt,
      items: run.result.items,
    });
    const data: ExportDialogData = {
      rowCount: protocol.rows.length,
      filtered: false,
      // The protocol is always the whole run — a scope choice would make no sense here.
      selectionCount: 0,
      noticeKeys: [],
    };
    this.dialog
      .open<ExportChoice | undefined>(ExportDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
        ariaLabelledBy: 'export-dialog-title',
      })
      .closed.subscribe((choice) => {
        if (choice?.format === 'csv') {
          downloadFile(
            purgeRunFilename(run.channelName, protocol.meta.finishedAt, 'csv'),
            purgeRunCsv(protocol),
            CSV_MIME,
          );
        } else if (choice?.format === 'json') {
          downloadFile(
            purgeRunFilename(run.channelName, protocol.meta.finishedAt, 'json'),
            purgeRunJson(protocol),
            JSON_MIME,
          );
        }
        if (choice) {
          this.protocolSaved.set(true);
        }
      });
  }

  protected openRestoreConfirm(): void {
    const run = this.deleteService.lastRun();
    if (!run || this.restoreService.isRunning()) {
      return;
    }
    const doneItems = run.result.items.filter((item) => item.status === 'done');
    if (doneItems.length === 0) {
      return;
    }

    if (!this.tokenService.hasToken()) {
      const promptRef = this.dialog.open<boolean>(SevenTvTokenPromptDialog, {
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
      });
      promptRef.closed.subscribe((saved) => {
        if (saved) {
          this.openRestoreConfirmDialog(doneItems);
        }
      });
      return;
    }
    this.openRestoreConfirmDialog(doneItems);
  }

  private openRestoreConfirmDialog(
    doneItems: { emoteId: string; sevenTvEmoteId: string; name: string }[],
  ): void {
    // Live slot view, so the projection line pops in once the check answers (the dialog is
    // already open by then) — same pattern as the delete confirm's shared-set warning.
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
      names: doneItems.map((item) => item.name),
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
          this.restoreService.startRestore(
            this.setId(),
            this.channelName(),
            doneItems.map((item) => ({
              emoteId: item.emoteId,
              sevenTvEmoteId: item.sevenTvEmoteId,
              name: item.name,
            })),
          );
        }
      });
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
