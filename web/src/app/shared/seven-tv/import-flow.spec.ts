import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { ImportConfirmDialogData, ImportConfirmOutcome } from './import-confirm-dialog';
import { ImportFlowDeps, startImportFlow } from './import-flow';

/**
 * `startImportFlow` opens a dialog through the plain `Dialog` object it is handed and never
 * injects anything itself — see the class doc on `ImportFlowDeps`. That is what lets every test
 * here run without a `TestBed`: `dialog.open` is a bare `vi.fn()` that stands in for both the
 * confirm dialog and, when needed, the token prompt, distinguished by call order (first call is
 * always the confirm dialog).
 */

function source(rows: ImportRow[] = [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }]): ImportSource {
  return {
    origin: { kind: 'channel', channelName: 'origin-channel' },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

function readyStatus(overrides: Partial<EmoteSetStatus> = {}): EmoteSetStatus {
  return {
    activeEmoteSetId: 'set-1',
    capacity: 1000,
    occupiedSlots: 10,
    trackedSince: '2026-01-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    botsExcludedSince: null,
    sharedChatSeparatedSince: null,
    ...overrides,
  };
}

interface Harness {
  deps: ImportFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  /** One entry per `loadImportTarget` call — `emoteAdminService.getSetStatus` is the one blocking
   *  request kept under manual control; `listEmotes`/`getSetWarning` resolve synchronously so only
   *  this subject gates when a load settles (needed to pin down the generation counter). */
  statusSubjects: Subject<EmoteSetStatus>[];
  /** Backs both `loadImportTarget`'s dialog-open-time fetch (used to build the preview) *and*
   *  the fresh #149/T5 re-check run right before `startImport` — resolves synchronously to an
   *  empty target set by default, i.e. neither one filters anything, unless a test overrides it. */
  listEmotes: ReturnType<typeof vi.fn>;
  startImport: ReturnType<typeof vi.fn>;
  hasToken: WritableSignal<boolean>;
  activeRun: WritableSignal<SevenTvRunKind | null>;
}

function setup(): Harness {
  const statusSubjects: Subject<EmoteSetStatus>[] = [];
  const listEmotes = vi.fn(() => of<EmoteListItem[]>([]));
  const emoteAdminService = {
    getSetStatus: vi.fn(() => {
      const subject = new Subject<EmoteSetStatus>();
      statusSubjects.push(subject);
      return subject;
    }),
    // Resolves synchronously and is irrelevant to every test below beyond letting forkJoin settle
    // as soon as the status subject does — only that one is kept under manual control.
    listEmotes,
    getSetWarning: vi.fn(() =>
      of({
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
    ),
  } as unknown as EmoteAdminService;

  const hasToken = signal(true);
  const tokenService = { hasToken } as unknown as SevenTvTokenService;

  const startImport = vi.fn();
  const importService = { startImport } as unknown as SevenTvImportService;

  const activeRun = signal<SevenTvRunKind | null>(null);
  const arbiter = { activeRun } as unknown as SevenTvRunArbiter;

  const dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));
  const dialog = { open: dialogOpen } as unknown as Dialog;

  return {
    deps: { dialog, emoteAdminService, tokenService, importService, arbiter },
    dialogOpen,
    statusSubjects,
    listEmotes,
    startImport,
    hasToken,
    activeRun,
  };
}

/** The `data` the flow handed to `openImportConfirmDialog` — call #0 of `dialog.open`. */
function confirmData(dialogOpen: ReturnType<typeof vi.fn>): ImportConfirmDialogData {
  return dialogOpen.mock.calls[0][1].data as ImportConfirmDialogData;
}

function confirmClosed(
  dialogOpen: ReturnType<typeof vi.fn>,
): Subject<ImportConfirmOutcome | undefined> {
  return dialogOpen.mock.results[0].value.closed as Subject<ImportConfirmOutcome | undefined>;
}

function tokenPromptClosed(dialogOpen: ReturnType<typeof vi.fn>): Subject<boolean> {
  return dialogOpen.mock.results[1].value.closed as Subject<boolean>;
}

describe('startImportFlow', () => {
  it('opens the confirm dialog with the target loading, and starts the load immediately', () => {
    const { deps, dialogOpen, statusSubjects } = setup();

    startImportFlow(deps, source(), 'target-channel');

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(confirmData(dialogOpen).target()).toEqual({ status: 'loading' });
    expect(statusSubjects).toHaveLength(1);
  });

  it('resolves the target to ready once the load answers', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), 'target-channel');

    statusSubjects[0].next(readyStatus({ activeEmoteSetId: 'set-9' }));
    statusSubjects[0].complete();

    expect(confirmData(dialogOpen).target()).toMatchObject({ status: 'ready', setId: 'set-9' });
  });

  it('resolves the target to failed when a blocking request errors with a non-404 status', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), 'target-channel');

    statusSubjects[0].error(new HttpErrorResponse({ status: 500 }));

    expect(confirmData(dialogOpen).target()).toEqual({ status: 'failed' });
  });

  it('resolves the target to no-set when a blocking request 404s', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), 'target-channel');

    statusSubjects[0].error(new HttpErrorResponse({ status: 404 }));

    expect(confirmData(dialogOpen).target()).toEqual({ status: 'no-set' });
  });

  it('drops a stale load answer after a retry, and applies the newer one once it lands', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), 'target-channel');
    const data = confirmData(dialogOpen);

    data.retry();
    expect(statusSubjects).toHaveLength(2);

    // The superseded (first) request finally answers — must be ignored, target stays loading
    // because the current (second) request has not answered yet.
    statusSubjects[0].next(readyStatus({ activeEmoteSetId: 'stale-set' }));
    statusSubjects[0].complete();
    expect(data.target()).toEqual({ status: 'loading' });

    // The current request answers — this one applies.
    statusSubjects[1].next(readyStatus({ activeEmoteSetId: 'fresh-set' }));
    statusSubjects[1].complete();
    expect(data.target()).toMatchObject({ status: 'ready', setId: 'fresh-set' });
  });

  it('ignores a load answer that arrives after the confirm dialog has already closed', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), 'target-channel');
    const data = confirmData(dialogOpen);

    confirmClosed(dialogOpen).next(undefined);

    statusSubjects[0].next(readyStatus());
    statusSubjects[0].complete();

    expect(data.target()).toEqual({ status: 'loading' });
  });

  it('exposes runBlocked as a live view of the arbiter, for the dialog to disable its own button', () => {
    const { deps, dialogOpen, activeRun } = setup();
    startImportFlow(deps, source(), 'target-channel');
    const data = confirmData(dialogOpen);

    expect(data.runBlocked()).toBe(false);

    activeRun.set('restore');

    expect(data.runBlocked()).toBe(true);
  });

  it('starts the import immediately when confirmed and a 7TV token is already stored', () => {
    const { deps, dialogOpen, startImport } = setup();
    const src = source();
    startImportFlow(deps, src, 'target-channel');

    const outcome: ImportConfirmOutcome = {
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    };
    confirmClosed(dialogOpen).next(outcome);

    // Fourth argument is the fresh #149/T5 re-check's skip count — 0 here because the harness's
    // default `listEmotes` reports an empty target set, so nothing gets filtered a second time.
    // Fifth is whether that check actually ran — true, since the fetch succeeded (#149).
    expect(startImport).toHaveBeenCalledWith(
      { setId: 'set-1', channelName: 'target-channel' },
      src.origin,
      outcome.rows,
      0,
      true,
    );
    // No second dialog — the token prompt is only for a missing token.
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  // #149/T5: `outcome.rows` already passed `buildImportPreview`'s dialog-open-time filter — this
  // pins the *second*, fresh check that runs right before the send, catching a row that became a
  // duplicate only after the dialog opened (another editor, another tab, a long-open dialog).
  describe('fresh pre-send duplicate check (#149/T5)', () => {
    it('re-checks the target set fresh at confirm time, not reusing the dialog-open snapshot', () => {
      const { deps, dialogOpen, listEmotes } = setup();
      startImportFlow(deps, source(), 'target-channel');

      // The dialog-open-time fetch (for the preview) has already happened by now.
      expect(listEmotes).toHaveBeenCalledTimes(1);

      confirmClosed(dialogOpen).next({
        targetSetId: 'set-1',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      // A second, independent fetch — the fresh check — runs right here, at confirm time.
      expect(listEmotes).toHaveBeenCalledTimes(2);
      expect(listEmotes).toHaveBeenLastCalledWith('target-channel');
    });

    it('drops a row that appeared in the target set only after the dialog opened, and reports it as skipped', () => {
      const { deps, dialogOpen, startImport, listEmotes } = setup();
      startImportFlow(deps, source(), 'target-channel');

      // Between the dialog opening (still-empty target, from setup()'s default) and the user
      // confirming, another editor added this exact emote to the target set under a different
      // alias — the dialog-time preview never saw it.
      listEmotes.mockReturnValue(
        of<EmoteListItem[]>([{ sevenTvEmoteId: '7tv-1', name: 'SomeAlias' }]),
      );

      confirmClosed(dialogOpen).next({
        targetSetId: 'set-1',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        { setId: 'set-1', channelName: 'target-channel' },
        source().origin,
        [],
        1,
        true,
      );
    });

    // #149: a failed check must fail open (every row still goes through, the run still starts) but
    // must not read as a clean all-clear — the flow forwards `available: false` from the filter
    // straight into `startImport`'s fifth argument rather than swallowing it.
    it('fails open on a failed duplicate check and reports it as unavailable rather than a clean skip', () => {
      const { deps, dialogOpen, startImport, listEmotes } = setup();
      startImportFlow(deps, source(), 'target-channel');

      listEmotes.mockReturnValue(throwError(() => new Error('network error')));

      confirmClosed(dialogOpen).next({
        targetSetId: 'set-1',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        { setId: 'set-1', channelName: 'target-channel' },
        source().origin,
        [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
        0,
        false,
      );
    });
  });

  it('starts nothing when the confirm dialog is dismissed without an outcome', () => {
    const { deps, dialogOpen, startImport } = setup();
    startImportFlow(deps, source(), 'target-channel');

    confirmClosed(dialogOpen).next(undefined);

    expect(startImport).not.toHaveBeenCalled();
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startImport, activeRun } = setup();
    activeRun.set('delete');
    startImportFlow(deps, source(), 'target-channel');

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });

    expect(startImport).not.toHaveBeenCalled();
  });

  it('prompts for a 7TV token when none is stored, and starts only once the prompt confirms', () => {
    const { deps, dialogOpen, startImport, hasToken } = setup();
    hasToken.set(false);
    startImportFlow(deps, source(), 'target-channel');

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect(startImport).not.toHaveBeenCalled();

    tokenPromptClosed(dialogOpen).next(true);

    expect(startImport).toHaveBeenCalledTimes(1);
  });

  it('does not start the import when the token prompt is dismissed', () => {
    const { deps, dialogOpen, startImport, hasToken } = setup();
    hasToken.set(false);
    startImportFlow(deps, source(), 'target-channel');

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });
    tokenPromptClosed(dialogOpen).next(false);

    expect(startImport).not.toHaveBeenCalled();
  });
});
