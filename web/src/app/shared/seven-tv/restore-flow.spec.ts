import { Dialog } from '@angular/cdk/dialog';
import { signal, WritableSignal } from '@angular/core';
import { of, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
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
    ...overrides,
  };
}

interface Harness {
  deps: RestoreFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  getSetStatus: ReturnType<typeof vi.fn>;
  startRestore: ReturnType<typeof vi.fn>;
  hasToken: WritableSignal<boolean>;
  activeRun: WritableSignal<SevenTvRunKind | null>;
}

function setup(): Harness {
  const getSetStatus = vi.fn(() => of(readyStatus()));
  const emoteAdminService = { getSetStatus } as unknown as EmoteAdminService;

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
    startRestore,
    hasToken,
    activeRun,
  };
}

/** The `data` a call to `dialog.open` was handed — only ever used for the confirm dialog, whatever
 *  its call index. */
function confirmData(
  dialogOpen: ReturnType<typeof vi.fn>,
  index: number,
): RestoreConfirmDialogData {
  return dialogOpen.mock.calls[index][1].data as RestoreConfirmDialogData;
}

function closedAt<T>(dialogOpen: ReturnType<typeof vi.fn>, index: number): Subject<T> {
  return dialogOpen.mock.results[index].value.closed as Subject<T>;
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

    closedAt<boolean>(dialogOpen, 0).next(true);

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
    closedAt<boolean>(dialogOpen, 0).next(true);

    expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [
      { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
    ]);
  });

  it('never opens the confirmation and never runs when the token prompt is cancelled', () => {
    const { deps, dialogOpen, getSetStatus, startRestore, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    closedAt<boolean>(dialogOpen, 0).next(false);

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('does not run when the confirmation is cancelled', () => {
    const { deps, dialogOpen, startRestore } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    closedAt<boolean>(dialogOpen, 0).next(false);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startRestore, activeRun } = setup();
    activeRun.set('import');

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    closedAt<boolean>(dialogOpen, 0).next(true);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('shows no slot projection once the set has no reported capacity', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ capacity: null })));

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(confirmData(dialogOpen, 0).slots()).toBeNull();
  });

  it('shows no slot projection when the status request errors', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    const status$ = new Subject<EmoteSetStatus>();
    getSetStatus.mockReturnValue(status$);

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    status$.error(new Error('boom'));

    expect(confirmData(dialogOpen, 0).slots()).toBeNull();
  });
});
