import { Dialog } from '@angular/cdk/dialog';
import { signal, WritableSignal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow } from '../export/purge-run-export';
import { RestoreConfirmDialogData } from './restore-confirm-dialog';
import { RestoreFlowDeps, startRestoreFlow } from './restore-flow';

/**
 * `startRestoreFlow` opens dialogs through the plain `Dialog` object it is handed and never
 * injects anything itself — see the class doc on `RestoreFlowDeps`. That is what lets every test
 * here run without a `TestBed`, same as `import-flow.spec.ts`: `dialog.open` is a bare `vi.fn()`
 * standing in for both the token prompt and the restore confirmation, distinguished by call order
 * — which one is call #0 depends on whether a token is already stored, so each test sets that up
 * explicitly rather than assuming a fixed position.
 */

const CHANNEL = 'frozen-channel';
const SET_ID = 'frozen-set';

function rows(): PurgeRunRow[] {
  return [
    { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done', errorMessage: null },
  ];
}

function readyStatus(overrides: Partial<EmoteSetStatus> = {}): EmoteSetStatus {
  return {
    activeEmoteSetId: SET_ID,
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
  deps: RestoreFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  getSetStatus: ReturnType<typeof vi.fn>;
  /** The pre-run duplicate check (#149/T5) — defaults to reporting an empty target set, i.e. no
   *  row gets filtered, unless a test overrides it. */
  listEmotes: ReturnType<typeof vi.fn>;
  startRestore: ReturnType<typeof vi.fn>;
  hasToken: WritableSignal<boolean>;
  activeRun: WritableSignal<SevenTvRunKind | null>;
}

function setup(): Harness {
  const getSetStatus = vi.fn(() => of(readyStatus()));
  const listEmotes = vi.fn(() => of<EmoteListItem[]>([]));
  const emoteAdminService = { getSetStatus, listEmotes } as unknown as EmoteAdminService;

  const hasToken = signal(true);
  const tokenService = { hasToken } as unknown as SevenTvTokenService;

  const startRestore = vi.fn();
  const restoreService = { startRestore } as unknown as SevenTvRestoreService;

  const activeRun = signal<SevenTvRunKind | null>(null);
  const arbiter = { activeRun } as unknown as SevenTvRunArbiter;

  const dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));
  const dialog = { open: dialogOpen } as unknown as Dialog;

  return {
    deps: { dialog, emoteAdminService, tokenService, restoreService, arbiter },
    dialogOpen,
    getSetStatus,
    listEmotes,
    startRestore,
    hasToken,
    activeRun,
  };
}

/** The `data` the first call to `dialog.open` was handed. Every test that reads it has a token
 *  stored, so that first call IS the confirm dialog. */
function confirmData(dialogOpen: ReturnType<typeof vi.fn>): RestoreConfirmDialogData {
  return dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
}

/** The `closed` subject of the first `dialog.open` call — the token prompt when none is stored,
 *  the confirmation otherwise. */
function firstClosed<T>(dialogOpen: ReturnType<typeof vi.fn>): Subject<T> {
  return dialogOpen.mock.results[0].value.closed as Subject<T>;
}

describe('startRestoreFlow', () => {
  it('prompts for a token first when none is stored, before touching the set or the run', () => {
    const { deps, dialogOpen, getSetStatus, startRestore, hasToken } = setup();
    hasToken.set(false);

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('opens the confirmation with the frozen channel once the token prompt confirms', () => {
    const { deps, dialogOpen, getSetStatus, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    firstClosed<boolean>(dialogOpen).next(true);

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('goes straight to the confirmation when a token is already stored', () => {
    const { deps, dialogOpen, getSetStatus } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('starts the restore with the frozen set id, the frozen channel and the given rows once confirmed', () => {
    const { deps, dialogOpen, startRestore } = setup();
    const theRows = rows();

    startRestoreFlow(deps, CHANNEL, SET_ID, theRows);
    firstClosed<boolean>(dialogOpen).next(true);

    // Fourth argument is the duplicate check's skip count (#149/T5) — 0 here because the harness's
    // default `listEmotes` reports an empty target set, so nothing gets filtered. Fifth is whether
    // that check actually ran — true, since the fetch succeeded (#149).
    expect(startRestore).toHaveBeenCalledWith(
      SET_ID,
      CHANNEL,
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
      0,
      true,
    );
  });

  // #149/T5: restore never had any duplicate protection — these two pin the fix in from the flow
  // layer down (the filtering logic itself is `already-present-filter.spec.ts`'s job).
  describe('duplicate protection (#149/T5)', () => {
    it('checks the target set fresh, right at confirm time, not from an earlier snapshot', () => {
      const { deps, dialogOpen, listEmotes } = setup();

      startRestoreFlow(deps, CHANNEL, SET_ID, rows());
      // The set-status fetch for the slot preview runs on dialog-open — the duplicate check must
      // not have run yet at that point, only once the user actually confirms.
      expect(listEmotes).not.toHaveBeenCalled();

      firstClosed<boolean>(dialogOpen).next(true);

      expect(listEmotes).toHaveBeenCalledWith(CHANNEL);
    });

    it('drops a row already present in the target set and reports it as skipped, queuing nothing else', () => {
      const { deps, dialogOpen, listEmotes, startRestore } = setup();
      listEmotes.mockReturnValue(of<EmoteListItem[]>([{ sevenTvEmoteId: '7tv-1', name: 'PogU' }]));

      startRestoreFlow(deps, CHANNEL, SET_ID, rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], 1, true);
    });

    // A second restore over the exact same protocol rows — e.g. the user runs restore, then runs
    // it again without anything having changed in between. Everything is already back in the set,
    // so nothing should be queued the second time.
    it('queues nothing on a second restore over rows already restored', () => {
      const { deps, dialogOpen, listEmotes, startRestore } = setup();
      const theRows = rows();
      listEmotes.mockReturnValue(
        of<EmoteListItem[]>(
          theRows.map((row) => ({ sevenTvEmoteId: row.sevenTvEmoteId, name: row.name })),
        ),
      );

      startRestoreFlow(deps, CHANNEL, SET_ID, theRows);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], theRows.length, true);
    });

    // #149: a failed check must fail open (every row still goes through, the run still starts) but
    // must not read as a clean all-clear — the flow forwards `available: false` from the filter
    // straight into `startRestore`'s fifth argument rather than swallowing it.
    it('fails open on a failed duplicate check and reports it as unavailable rather than a clean skip', () => {
      const { deps, dialogOpen, listEmotes, startRestore } = setup();
      listEmotes.mockReturnValue(throwError(() => new Error('network error')));
      const theRows = rows();

      startRestoreFlow(deps, CHANNEL, SET_ID, theRows);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
        0,
        false,
      );
    });
  });

  it('never opens the confirmation and never runs when the token prompt is cancelled', () => {
    const { deps, dialogOpen, getSetStatus, startRestore, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    firstClosed<boolean>(dialogOpen).next(false);

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('does not run when the confirmation is cancelled', () => {
    const { deps, dialogOpen, startRestore } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    firstClosed<boolean>(dialogOpen).next(false);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startRestore, activeRun } = setup();
    activeRun.set('import');

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    firstClosed<boolean>(dialogOpen).next(true);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('projects the answered slot numbers into the confirmation', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ occupiedSlots: 42, capacity: 600 })));

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });
  });

  it('shows no slot projection once the set has no reported capacity', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ capacity: null })));

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });

  // Deliberately emits a *usable* status before the error: `slots` starts out null, so a test that
  // only errored would stay green with the error handler deleted outright.
  it('drops the slot projection again when the status request errors after answering', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    const status$ = new Subject<EmoteSetStatus>();
    getSetStatus.mockReturnValue(status$);

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    status$.next(readyStatus({ occupiedSlots: 42, capacity: 600 }));
    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });

    status$.error(new Error('boom'));

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });
});
