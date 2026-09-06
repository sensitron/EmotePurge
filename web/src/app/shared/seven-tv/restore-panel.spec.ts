import { Dialog } from '@angular/cdk/dialog';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { ExportEnvelope } from '../export/export-envelope';
import { buildPurgeRunProtocol, purgeRunJson } from '../export/purge-run-export';
import { RestorePanel } from './restore-panel';

/**
 * `RestorePanel` opens its dialogs through the plain `Dialog` it injects, same as
 * `startImportFlow` (see `import-flow.spec.ts`): `dialog.open` is one `vi.fn()` standing in for
 * the token prompt, the restore confirmation and the import confirmation alike, distinguished by
 * call order and by the side effects (`getSetStatus`/`startRestore`/`startImport`) each step is
 * allowed to have triggered by the time it runs — never by reaching into dialog internals.
 */

// Only the keys this panel itself renders — not the full app translation file.
const DE_TRANSLATIONS = {
  restore: {
    import: {
      errors: {
        notJson: 'Die Datei ist kein gültiges JSON.',
        wrongKind:
          'Die Datei ist kein EmotePurge-Export. Importierbar sind Purge-Protokolle, Emote-Listen und Nutzungs-Exporte im JSON-Format.',
        votingExport:
          'Das ist ein Export einer Abstimmung, kein Purge-Protokoll. Ein importierbares Protokoll entsteht erst bei einem Löschlauf und wird direkt danach zum Download angeboten.',
        wrongChannel: 'Das Protokoll gehört zu einem anderen Channel.',
        wrongSet:
          'Das Protokoll gehört zu einem anderen Emote-Set — der Channel hat das aktive Set gewechselt.',
        noRestorableRows:
          'Das Protokoll enthält keine erfolgreich gelöschten Emotes zum Wiederherstellen.',
        noRows: 'Die Datei enthält keine importierbaren Emotes.',
      },
    },
  },
};

const CURRENT_CHANNEL = 'somechannel';
const CURRENT_SET = 'set-current';

/** Always resolves to exactly this string — sidesteps whatever `Blob`/`File.text()` support the
 *  test environment happens to have, per the task's "File object with `text()`, not real file I/O". */
function file(text: string, name = 'export.json'): File {
  const f = new File([text], name, { type: 'application/json' });
  Object.defineProperty(f, 'text', { value: () => Promise.resolve(text) });
  return f;
}

function purgeRunText(
  overrides: {
    channelName?: string;
    emoteSetId?: string;
    items?: (RunQueueItem & { emoteId: string })[];
  } = {},
): string {
  const protocol = buildPurgeRunProtocol({
    channelName: overrides.channelName ?? CURRENT_CHANNEL,
    emoteSetId: overrides.emoteSetId ?? CURRENT_SET,
    startedAt: Date.parse('2026-09-01T10:00:00Z'),
    finishedAt: Date.parse('2026-09-01T10:05:00Z'),
    items: overrides.items ?? [
      { key: 'e1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
    ],
  });
  return purgeRunJson(protocol);
}

function emoteListText(overrides: Partial<ExportEnvelope<unknown>> = {}): string {
  const envelope: ExportEnvelope<unknown> = {
    source: 'emotepurge',
    kind: 'emote-list',
    formatVersion: 1,
    exportedAt: '2026-09-01T10:00:00Z',
    channelName: 'otherchannel',
    withheld: [],
    meta: {},
    rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
    ...overrides,
  };
  return JSON.stringify(envelope);
}

function votingText(): string {
  return JSON.stringify({
    source: 'emotepurge',
    kind: 'voting',
    formatVersion: 1,
    exportedAt: '2026-09-01T10:00:00Z',
    channelName: 'otherchannel',
    withheld: [],
    meta: {},
    rows: [],
  });
}

function readyStatus(overrides: Partial<EmoteSetStatus> = {}): EmoteSetStatus {
  return {
    activeEmoteSetId: CURRENT_SET,
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
  fixture: ComponentFixture<RestorePanel>;
  detect(): void;
  text(): string;
  triggerDisabled(): boolean;
  /** Drives `onFileSelected` directly with a synthetic `Event`/`<input>` pair, awaiting the whole
   *  (async) handler — a real `dispatchEvent('change')` would leave its `await file.text()` still
   *  in flight with nothing in this zoneless setup to signal when it settles. */
  selectFile(selected: File): Promise<void>;
}

describe('RestorePanel', () => {
  let getSetStatus: ReturnType<typeof vi.fn>;
  let listEmotes: ReturnType<typeof vi.fn>;
  let getSetWarning: ReturnType<typeof vi.fn>;
  let startRestore: ReturnType<typeof vi.fn>;
  let startImport: ReturnType<typeof vi.fn>;
  let hasToken: WritableSignal<boolean>;
  let activeRun: WritableSignal<SevenTvRunKind | null>;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    getSetStatus = vi.fn(() => of(readyStatus()));
    listEmotes = vi.fn(() => of([]));
    getSetWarning = vi.fn(() =>
      of({
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
    );
    startRestore = vi.fn();
    startImport = vi.fn();
    hasToken = signal(true);
    activeRun = signal<SevenTvRunKind | null>(null);
    dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));

    await TestBed.configureTestingModule({
      imports: [
        RestorePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: EmoteAdminService,
          useValue: { getSetStatus, listEmotes, getSetWarning } as unknown as EmoteAdminService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: { startRestore } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvImportService,
          useValue: { startImport } as unknown as SevenTvImportService,
        },
        { provide: SevenTvTokenService, useValue: { hasToken } as unknown as SevenTvTokenService },
        { provide: SevenTvRunArbiter, useValue: { activeRun } as unknown as SevenTvRunArbiter },
        { provide: Dialog, useValue: { open: dialogOpen } as unknown as Dialog },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(channelName = CURRENT_CHANNEL, setId = CURRENT_SET): Harness {
    const fixture = TestBed.createComponent(RestorePanel);
    fixture.componentRef.setInput('channelName', channelName);
    fixture.componentRef.setInput('setId', setId);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    return {
      fixture,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      triggerDisabled: () => {
        const button = host.querySelector('button');
        if (!button) {
          throw new Error('no trigger button rendered');
        }
        return button.disabled;
      },
      selectFile: async (selected) => {
        const input = document.createElement('input');
        Object.defineProperty(input, 'files', { value: [selected], configurable: true });
        const event = { target: input } as unknown as Event;
        const instance = fixture.componentInstance as unknown as {
          onFileSelected(e: Event): Promise<void>;
        };
        await instance.onFileSelected(event);
        fixture.detectChanges();
      },
    };
  }

  /** The `closed` subject a dialog was opened with — call `index` of `dialog.open`. */
  function openedClosed<T>(index: number): Subject<T> {
    return dialogOpen.mock.results[index].value.closed as Subject<T>;
  }

  describe('dispatch by envelope kind (#72, K3 / DECISIONS "Restore-Panel bekommt einen dritten Dispatch-Zweig")', () => {
    it('routes a purge-run protocol through the restore path, token prompt before the confirmation', async () => {
      hasToken.set(false);
      const dialog = render();

      await dialog.selectFile(file(purgeRunText()));

      // Missing token: only the token prompt has opened so far, and the restore-specific work
      // (the slot preview read) has not started — proof that the confirmation has not run yet.
      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(getSetStatus).not.toHaveBeenCalled();
      expect(startRestore).not.toHaveBeenCalled();

      openedClosed<boolean>(0).next(true);
      dialog.detect();

      // The confirmation opens only now, and the slot preview is fetched for it.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);

      openedClosed<boolean>(1).next(true);

      expect(startRestore).toHaveBeenCalledWith(CURRENT_SET, CURRENT_CHANNEL, [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
      ]);
    });

    it('skips the token prompt for a purge-run protocol when a token is already stored', async () => {
      hasToken.set(true);
      const dialog = render();

      await dialog.selectFile(file(purgeRunText()));

      // Goes straight to the confirmation — one dialog, and it is already the confirm one.
      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);

      openedClosed<boolean>(0).next(true);

      expect(startRestore).toHaveBeenCalledTimes(1);
    });

    it('never restores when the token prompt is cancelled', async () => {
      hasToken.set(false);
      const dialog = render();
      await dialog.selectFile(file(purgeRunText()));

      openedClosed<boolean>(0).next(false);
      dialog.detect();

      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(startRestore).not.toHaveBeenCalled();
    });

    it('never restores when the confirmation is cancelled', async () => {
      const dialog = render();
      await dialog.selectFile(file(purgeRunText()));

      openedClosed<boolean>(0).next(false);

      expect(startRestore).not.toHaveBeenCalled();
    });

    it('routes an emote-list file through the import path, confirmation before the token prompt', async () => {
      hasToken.set(false);
      const dialog = render();

      // Exported from a *different* channel — the target must still be the current one (below).
      await dialog.selectFile(file(emoteListText({ channelName: 'otherchannel' })));

      // Reversed order from the purge-run path: the confirmation is already open, and the token
      // is not asked for yet.
      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);
      expect(startImport).not.toHaveBeenCalled();

      openedClosed<{ targetSetId: string; rows: unknown[] }>(0).next({
        targetSetId: CURRENT_SET,
        rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
      });
      dialog.detect();

      // Only now, after the confirmation, does the panel's own missing-token case appear.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(startImport).not.toHaveBeenCalled();

      openedClosed<boolean>(1).next(true);

      // The target is always the *current* channel, never the file's origin (R2/DECISIONS).
      expect(startImport).toHaveBeenCalledWith(
        { setId: CURRENT_SET, channelName: CURRENT_CHANNEL },
        expect.objectContaining({ kind: 'file', channelName: 'otherchannel' }),
        [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
      );
    });

    it('routes a usage export through the same import path as an emote-list file', async () => {
      hasToken.set(true);
      const dialog = render();

      await dialog.selectFile(
        file(
          emoteListText({
            kind: 'usage',
            rows: [{ sevenTvEmoteId: '7tv-1', emoteName: 'PogU', totalUseCount: 3 }],
          }),
        ),
      );

      expect(dialogOpen).toHaveBeenCalledTimes(1);

      openedClosed<{ targetSetId: string; rows: unknown[] }>(0).next({
        targetSetId: CURRENT_SET,
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }],
      });

      // Token already stored: no second dialog, the panel does not prompt a second time on top of
      // the flow's own (already-satisfied) check.
      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(startImport).toHaveBeenCalledTimes(1);
    });

    it('does not start the import when its confirmation is dismissed without an outcome', async () => {
      const dialog = render();
      await dialog.selectFile(file(emoteListText()));

      openedClosed<undefined>(0).next(undefined);

      expect(startImport).not.toHaveBeenCalled();
      expect(dialogOpen).toHaveBeenCalledTimes(1);
    });

    it('rejects a voting export outright, with no restore or import path for it', async () => {
      const dialog = render();

      await dialog.selectFile(file(votingText()));

      expect(dialog.text()).toContain('Das ist ein Export einer Abstimmung, kein Purge-Protokoll.');
      expect(dialogOpen).not.toHaveBeenCalled();
    });
  });

  describe('purge-run validation (parsePurgeRunProtocol, applied against the current channel/set)', () => {
    it('rejects a protocol from a different channel before ever opening a dialog', async () => {
      const dialog = render();

      await dialog.selectFile(file(purgeRunText({ channelName: 'otherchannel' })));

      expect(dialog.text()).toContain('Das Protokoll gehört zu einem anderen Channel.');
      expect(dialogOpen).not.toHaveBeenCalled();
    });

    it('rejects a protocol for a set that is no longer the active one', async () => {
      const dialog = render();

      await dialog.selectFile(file(purgeRunText({ emoteSetId: 'set-old' })));

      expect(dialog.text()).toContain(
        'Das Protokoll gehört zu einem anderen Emote-Set — der Channel hat das aktive Set gewechselt.',
      );
      expect(dialogOpen).not.toHaveBeenCalled();
    });

    it('restores only the rows the purge actually deleted, dropping failed/cancelled ones', async () => {
      const dialog = render();
      await dialog.selectFile(
        file(
          purgeRunText({
            items: [
              { key: 'e1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
              {
                key: 'e2',
                emoteId: 'e2',
                sevenTvEmoteId: '7tv-2',
                name: 'KEKW',
                status: 'failed',
                errorMessage: 'boom',
              },
            ],
          }),
        ),
      );

      openedClosed<boolean>(0).next(true);

      expect(startRestore).toHaveBeenCalledWith(CURRENT_SET, CURRENT_CHANNEL, [
        { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
      ]);
    });
  });

  describe('file-format rejection (readEnvelope/parseImportSource, before any dialog)', () => {
    it('rejects a file that is not valid JSON', async () => {
      const dialog = render();

      await dialog.selectFile(file('not json{'));

      expect(dialog.text()).toContain('Die Datei ist kein gültiges JSON.');
      expect(dialogOpen).not.toHaveBeenCalled();
    });

    it('rejects an emote-list file whose rows are all invalid', async () => {
      const dialog = render();

      await dialog.selectFile(file(emoteListText({ rows: [{ sevenTvEmoteId: '', name: 'x' }] })));

      expect(dialog.text()).toContain('Die Datei enthält keine importierbaren Emotes.');
      expect(dialogOpen).not.toHaveBeenCalled();
    });

    it('resets a previous error banner on every new attempt, regardless of the new outcome', async () => {
      const dialog = render();

      await dialog.selectFile(file(purgeRunText({ channelName: 'otherchannel' })));
      expect(dialog.text()).toContain('Das Protokoll gehört zu einem anderen Channel.');

      await dialog.selectFile(file(purgeRunText()));

      expect(dialog.text()).not.toContain('Das Protokoll gehört zu einem anderen Channel.');
      expect(dialogOpen).toHaveBeenCalledTimes(1);
    });
  });

  describe('trigger lock while a 7TV run is active elsewhere', () => {
    it('disables the trigger exactly while arbiter.activeRun() is not null', () => {
      const dialog = render();
      expect(dialog.triggerDisabled()).toBe(false);

      activeRun.set('delete');
      dialog.detect();
      expect(dialog.triggerDisabled()).toBe(true);

      activeRun.set(null);
      dialog.detect();
      expect(dialog.triggerDisabled()).toBe(false);
    });
  });
});
