import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import { fileImportTriggerDisabled } from './file-import-trigger-gate';
import { openFileImportDialog } from './file-import-dialog';
import { startImportFlow } from './import-flow';
import { startRestoreFlow } from './restore-flow';

/**
 * The header button that opens the file-based restore/import path (#91, plan §1.1): freezes
 * `channelName`/`setId` at the moment of the click, opens `FileImportDialog` to read and validate
 * the chosen file, then hands the result to whichever chain fits — `startRestoreFlow` for a
 * purge-run protocol, `startImportFlow` (targeting the frozen channel) for an emote-list or usage
 * export. Neither chain runs from inside the still-open file-import dialog: that dialog always
 * closes first (its own contract, see `FileImportResult`), so the app's one-dialog-at-a-time rule
 * (`shared/ui/dialog.ts`) holds and each chain keeps its own dialog ordering — Restore asks for the
 * 7TV token before the confirmation, Import only after it (`startImportFlow`'s doc explains why;
 * this trigger must not prompt for a token itself on top of either).
 *
 * Injects its own services (#70/#91) — the page it sits in gets no new method of its own, which
 * keeps the page's own coverage surface small (see plan 2.4).
 *
 * `importScopeCurrent` is an input rather than something computed here from page state, so the
 * lock this button carries stays a pure function of two booleans (`fileImportTriggerDisabled`,
 * testable without a TestBed) — the page computes the boolean itself, the same way it already does
 * for the neighbouring "Übertragen…" button (`importScopeIsCurrent`). `atlasOrder().length === 0`
 * and `!isCoarse()` deliberately do NOT appear here: both are already enforced by the `@if` block
 * this trigger is placed inside on the page, alongside "Übertragen…" (plan §1.2 point 3).
 */
@Component({
  selector: 'app-file-import-trigger',
  imports: [Button, TranslocoPipe],
  template: `
    <button
      type="button"
      appButton="neutral"
      class="disabled:cursor-not-allowed"
      [disabled]="disabled()"
      (click)="openDialog()"
    >
      {{ 'restore.import.trigger' | transloco }}
    </button>
  `,
})
export class FileImportTrigger {
  readonly channelName = input.required<string>();
  /** The channel's *current* active set — a purge-run protocol is validated against it. */
  readonly setId = input.required<string>();
  /** See `importScopeIsCurrent` on the page; defaults to true so a caller that has no such window
   *  to guard against (there is currently only one, the usage-stats page) need not pass it. */
  readonly importScopeCurrent = input(true);

  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly dialog = inject(Dialog);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly importService = inject(SevenTvImportService);

  protected readonly disabled = computed(() =>
    fileImportTriggerDisabled({
      hasActiveRun: this.arbiter.activeRun() !== null,
      importScopeCurrent: this.importScopeCurrent(),
    }),
  );

  protected openDialog(): void {
    // Frozen here, at the click — never read again from the live inputs below, so a channel switch
    // while a dialog further down either chain is still open cannot retarget what gets read,
    // validated or restored/imported (plan §1.5).
    const channelName = this.channelName();
    const setId = this.setId();

    openFileImportDialog(this.dialog, { channelName, setId }).closed.subscribe((result) => {
      if (!result) {
        return;
      }
      if (result.kind === 'restore') {
        startRestoreFlow(
          {
            dialog: this.dialog,
            emoteAdminService: this.emoteAdminService,
            tokenService: this.tokenService,
            restoreService: this.restoreService,
            arbiter: this.arbiter,
          },
          channelName,
          setId,
          result.rows,
        );
        return;
      }
      startImportFlow(
        {
          dialog: this.dialog,
          emoteAdminService: this.emoteAdminService,
          tokenService: this.tokenService,
          importService: this.importService,
          arbiter: this.arbiter,
        },
        result.source,
        channelName,
      );
    });
  }
}
