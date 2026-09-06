import { Dialog } from '@angular/cdk/dialog';
import { computed, signal } from '@angular/core';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { ImportTargetLoadState, loadImportTarget } from '../../core/emotes/import-target-loader';
import { ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { ImportConfirmOutcome, openImportConfirmDialog } from './import-confirm-dialog';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/**
 * Everything the flow needs, handed in rather than injected. The flow opens dialogs, which live in
 * `shared/`, while the services it drives live in `core/` — and `core/` may not import from
 * `shared/`. A service here would therefore have to sit in `shared/` and be provided there, for a
 * piece of code that holds no state between calls; a function that takes its collaborators keeps
 * the dependency edges pointing the one legal way and lets both entry points (the usage-stats page
 * and the restore panel) pass their own injected instances.
 */
export interface ImportFlowDeps {
  dialog: Dialog;
  emoteAdminService: EmoteAdminService;
  tokenService: SevenTvTokenService;
  importService: SevenTvImportService;
  arbiter: SevenTvRunArbiter;
}

/**
 * Confirms and starts one copy run: target data → confirmation → 7TV token → run.
 *
 * **The token prompt comes after the confirmation**, unlike the delete and the restore flows, which
 * ask for the token first. That is deliberate (#72, R2): the picker and this preview are pure reads,
 * and demanding a write secret before the user has seen what would happen asks for trust in the
 * wrong order — here, unlike in a restore, the preview *is* where the decision is made. Do not
 * "align" this with the other two flows.
 *
 * The target load starts before the dialog opens but the dialog does not wait for it: it renders
 * its skeleton immediately and fills in on the single emission `loadImportTarget` guarantees (R8).
 */
export function startImportFlow(
  deps: ImportFlowDeps,
  source: ImportSource,
  targetChannelName: string,
): void {
  const target = signal<ImportTargetLoadState>({ status: 'loading' });

  // A retry while an earlier load is still in flight must not be overwritten by that older answer
  // — every load claims a generation and drops itself if it is no longer the current one. Closing
  // the dialog bumps the generation too, so a late answer writes into nothing after that.
  let generation = 0;

  const load = (): void => {
    const mine = ++generation;
    target.set({ status: 'loading' });
    loadImportTarget(deps.emoteAdminService, targetChannelName).subscribe((state) => {
      if (mine === generation) {
        target.set(state);
      }
    });
  };

  const start = (outcome: ImportConfirmOutcome): void => {
    // The engine only refuses *its own* second run; a delete or restore running elsewhere is
    // invisible to it, so the cross-kind check happens here — silently, because the progress of
    // that other run is already on screen and saying it twice would be the louder mistake.
    if (deps.arbiter.activeRun() !== null) {
      return;
    }
    deps.importService.startImport(
      { setId: outcome.targetSetId, channelName: targetChannelName },
      source.origin,
      outcome.rows,
    );
  };

  load();

  const confirmRef = openImportConfirmDialog(deps.dialog, {
    source,
    targetChannelName,
    target: target.asReadonly(),
    retry: load,
    runBlocked: computed(() => deps.arbiter.activeRun() !== null),
  });

  confirmRef.closed.subscribe((outcome) => {
    generation++;
    if (!outcome) {
      return;
    }
    if (deps.tokenService.hasToken()) {
      start(outcome);
      return;
    }
    openSevenTvTokenPromptDialog(deps.dialog).closed.subscribe((saved) => {
      if (saved === true) {
        start(outcome);
      }
    });
  });
}
