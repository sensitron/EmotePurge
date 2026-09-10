import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { Subject, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { ForeignChannelImportResult } from './foreign-channel-step';
import { buildForeignImportSource, startForeignChannelImportFlow } from './foreign-import-flow';
import { ImportFlowDeps } from './import-flow';

/**
 * Like `import-flow.spec.ts`, this runs without a `TestBed`: the flow opens dialogs through the
 * plain `Dialog` it is handed, so `dialog.open` is a `vi.fn()` standing in for every dialog of the
 * chain, distinguished by call order.
 */

function foreignRow(
  sevenTvEmoteId: string,
  name: string,
  defaultName = `${name}Global`,
): ForeignEmoteRow {
  return {
    sevenTvEmoteId,
    name,
    defaultName,
    imageUrl: `https://cdn.7tv.app/emote/${sevenTvEmoteId}/2x.webp`,
    topAllTime: null,
    trending: null,
  };
}

function picked(rows: ForeignEmoteRow[], channelName = 'handofblood'): ForeignChannelImportResult {
  return { channelName, sevenTvUserId: '7tv-user-1', emoteSetId: 'set-source', rows };
}

interface Harness {
  deps: ImportFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  startImport: ReturnType<typeof vi.fn>;
}

function setup(): Harness {
  const emoteAdminService = {
    getSetStatus: vi.fn(() => new Subject()),
    listEmotes: vi.fn(() => of([])),
    getSetWarning: vi.fn(() =>
      of({
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
    ),
  } as unknown as EmoteAdminService;

  const startImport = vi.fn();
  const dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));

  return {
    deps: {
      dialog: { open: dialogOpen } as unknown as Dialog,
      emoteAdminService,
      tokenService: { hasToken: signal(true) } as unknown as SevenTvTokenService,
      importService: { startImport } as unknown as SevenTvImportService,
      arbiter: { activeRun: signal<SevenTvRunKind | null>(null) } as unknown as SevenTvRunArbiter,
    },
    dialogOpen,
    startImport,
  };
}

function closedSubject<T>(dialogOpen: ReturnType<typeof vi.fn>, call: number): Subject<T> {
  return dialogOpen.mock.results[call].value.closed as Subject<T>;
}

describe('buildForeignImportSource', () => {
  it('takes over the source alias, not the global default name', () => {
    // The decision from the design doc, and the reason the collision hint matters for this source:
    // an alias the source channel invented is far likelier to be taken in the target set than a
    // global base name would be.
    const source = buildForeignImportSource(picked([foreignRow('e1', 'HandLuL', 'LuL')]));

    expect(source.rows).toEqual([{ sevenTvEmoteId: 'e1', name: 'HandLuL' }]);
  });

  it('marks the origin as the foreign 7TV channel it came from', () => {
    const source = buildForeignImportSource(picked([foreignRow('e1', 'Kappa')], 'HandOfBlood'));

    expect(source.origin).toEqual({ kind: 'seventv-channel', channelName: 'HandOfBlood' });
  });

  it('deduplicates by 7TV id and discards nothing', () => {
    // Nothing here can be invalid the way a parsed file's rows can: these rows came out of our own
    // endpoint already parsed. Dedup still runs, because the run engine never deduplicates.
    const source = buildForeignImportSource(
      picked([foreignRow('e1', 'Kappa'), foreignRow('e1', 'KappaAgain'), foreignRow('e2', 'PogU')]),
    );

    expect(source.rows).toEqual([
      { sevenTvEmoteId: 'e1', name: 'Kappa' },
      { sevenTvEmoteId: 'e2', name: 'PogU' },
    ]);
    expect(source.duplicatesCollapsed).toBe(1);
    expect(source.discardedRows).toBe(0);
  });
});

describe('startForeignChannelImportFlow', () => {
  it('opens the import confirmation directly — no target picker in between (#147)', () => {
    const { deps, dialogOpen } = setup();

    startForeignChannelImportFlow(deps, picked([foreignRow('e1', 'Kappa')]), 'my_channel');

    // Exactly one dialog, and it is the confirmation: where the emotes go was decided by the page
    // the flow was started from, exactly as it is for the file path.
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('hands the picked rows and the foreign origin to the ordinary import run', () => {
    const { deps, dialogOpen, startImport } = setup();

    startForeignChannelImportFlow(deps, picked([foreignRow('e1', 'Kappa')]), 'my_channel');
    closedSubject<unknown>(dialogOpen, 0).next({
      targetSetId: 'set-target',
      rows: [{ sevenTvEmoteId: 'e1', name: 'Kappa' }],
    });

    // Fourth argument is the #149/T5 fresh duplicate-check skip count — 0 because `listEmotes`
    // defaults to an empty target set. Fifth is whether that check actually ran — true, since the
    // fetch succeeded (#149).
    expect(startImport).toHaveBeenCalledWith(
      { setId: 'set-target', channelName: 'my_channel' },
      { kind: 'seventv-channel', channelName: 'handofblood' },
      [{ sevenTvEmoteId: 'e1', name: 'Kappa' }],
      0,
      true,
    );
  });

  it('starts no run when the confirmation is dismissed', () => {
    const { deps, dialogOpen, startImport } = setup();

    startForeignChannelImportFlow(deps, picked([foreignRow('e1', 'Kappa')]), 'my_channel');
    closedSubject<unknown>(dialogOpen, 0).next(undefined);

    expect(startImport).not.toHaveBeenCalled();
  });
});
