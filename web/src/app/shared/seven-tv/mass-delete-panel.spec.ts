import { Dialog } from '@angular/cdk/dialog';
import { Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunResult } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { CSV_MIME } from '../export/csv';
import { JSON_MIME } from '../export/export-envelope';
import { DeletableEmote, MassDeletePanel } from './mass-delete-panel';

/**
 * `openProtocolExport()`'s `downloadFile(...)` call is a real `<a download>` click against a real
 * `Blob`/object URL. The Angular unit-test system refuses `vi.mock` for relative imports, so this
 * spy sits at the same seam `file-download.spec.ts` already uses (`URL.createObjectURL`,
 * `document.createElement('a')`) rather than mocking the module — content is
 * `purge-run-export.spec.ts`'s job, this only pins which download a dialog choice produces.
 */
interface CapturedDownload {
  filename: string;
  mimeType: string;
}

/** Spies on the same two seams `downloadFile` touches — restore via `vi.restoreAllMocks()` in
 *  `afterEach`, matching `file-download.spec.ts`'s own pattern. */
function captureDownloads(): CapturedDownload[] {
  const downloads: CapturedDownload[] = [];
  if (!('createObjectURL' in URL)) {
    Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
  }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
    downloads.push({ filename: '', mimeType: (blob as Blob).type });
    return 'blob:test';
  });
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);

  const originalCreateElement = document.createElement.bind(document);
  vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
    const element = originalCreateElement(tag);
    if (tag === 'a') {
      vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => {
        const pending = downloads[downloads.length - 1];
        if (pending) {
          pending.filename = (element as HTMLAnchorElement).download;
        }
      });
    }
    return element;
  });
  return downloads;
}

/**
 * Only the row-composition contract (design doc §8.7): constructive group before the destructive
 * action, a neutral exit after it, and no leftover gap when the host page has nothing to project —
 * checked through accessible button names/order, never through the Tailwind classes that produce
 * the spacing (Regel 12). The delete/restore *flows* this panel also drives are exercised in the
 * specs of the pieces that own them (`restore-flow.spec.ts`, `import-trigger.spec.ts`), not
 * here.
 */

const DE_TRANSLATIONS = {
  massDelete: {
    deleteButton: 'Löschen ({{ count }})',
    clearSelection: 'Auswahl aufheben',
  },
};

const DELETE_LABEL = 'Löschen (2)';
const CLEAR_LABEL = 'Auswahl aufheben';
const VOTE_LABEL = 'Zur Abstimmung stellen';

const EMOTES: DeletableEmote[] = [
  { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

@Component({
  selector: 'app-host',
  imports: [MassDeletePanel],
  template: `
    <app-mass-delete-panel
      [setId]="setId"
      [channelName]="channelName"
      [selectedEmotes]="emotes"
      [leadingActionsPresent]="leadingActionsPresent"
    >
      @if (projectVoteButton) {
        <button type="button" selection-actions>{{ voteLabel }}</button>
      }
    </app-mass-delete-panel>
  `,
})
class HostComponent {
  setId = 'set-1';
  channelName = 'somechannel';
  emotes = EMOTES;
  leadingActionsPresent = false;
  projectVoteButton = false;
  voteLabel = VOTE_LABEL;
}

describe('MassDeletePanel row composition', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        HostComponent,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: EmoteAdminService,
          useValue: {} as unknown as EmoteAdminService,
        },
        {
          provide: SevenTvDeleteService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            lastRun: signal(null),
          } as unknown as SevenTvDeleteService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
          } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvRunArbiter,
          useValue: {
            activeRun: signal<SevenTvRunKind | null>(null),
          } as unknown as SevenTvRunArbiter,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: Dialog, useValue: { open: vi.fn() } as unknown as Dialog },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
  });

  function render(setup: Partial<HostComponent> = {}): {
    fixture: ComponentFixture<HostComponent>;
    buttonNames(): string[];
  } {
    const fixture = TestBed.createComponent(HostComponent);
    Object.assign(fixture.componentInstance, setup);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    return {
      fixture,
      buttonNames: () =>
        Array.from(host.querySelectorAll('button'))
          .map((button) => button.textContent?.trim() ?? '')
          .filter((text) => text.length > 0),
    };
  }

  it('renders only the destructive action and the neutral exit when nothing is projected (voting-detail default)', () => {
    const { buttonNames } = render({ leadingActionsPresent: false, projectVoteButton: false });

    expect(buttonNames()).toEqual([DELETE_LABEL, CLEAR_LABEL]);
  });

  it('keeps the destructive action and exit unchanged even if leadingActionsPresent is true but nothing was actually projected', () => {
    // leadingActionsPresent only controls the gap/grouping wrapper, not what appears inside the
    // slot — an empty projection still shows no leading button, just (invisibly) reserves the gap.
    const { buttonNames } = render({ leadingActionsPresent: true, projectVoteButton: false });

    expect(buttonNames()).toEqual([DELETE_LABEL, CLEAR_LABEL]);
  });

  it('orders the projected constructive group before the destructive action, and the neutral exit after it', () => {
    const { buttonNames } = render({ leadingActionsPresent: true, projectVoteButton: true });

    expect(buttonNames()).toEqual([VOTE_LABEL, DELETE_LABEL, CLEAR_LABEL]);
  });

  it('omits the neutral exit once the selection is empty, keeping the destructive action last among the rest', () => {
    const { buttonNames } = render({
      leadingActionsPresent: true,
      projectVoteButton: true,
      emotes: [],
    });

    // With an empty selection, "Löschen (0)" itself is still rendered (disabled elsewhere), but
    // "Auswahl aufheben" — the clear-selection escape hatch — has nothing left to clear.
    expect(buttonNames()).toEqual([VOTE_LABEL, 'Löschen (0)']);
  });
});

/**
 * `openProtocolExport()`'s dialog-choice handling (#141 follow-up, Regel 12): which download a
 * csv/json choice produces, that a cancel produces none, and that `protocolSaved` — the reminder
 * next to Close — only flips once a choice actually closed the dialog. Mounts `MassDeletePanel`
 * directly rather than through `HostComponent`, since `openProtocolExport` is called on the
 * component instance directly (same style as `usage-stats-page.spec.ts` calling protected
 * members) instead of driving the real `app-run-progress-panel` markup just to click a button.
 */
describe('MassDeletePanel — protocol export choice handling (#141)', () => {
  let fixture: ComponentFixture<MassDeletePanel>;
  let panel: MassDeletePanel;
  let openSpy: ReturnType<typeof vi.fn>;
  let lastRun: WritableSignal<{ setId: string; channelName: string; result: RunResult } | null>;
  let downloads: CapturedDownload[];

  beforeEach(async () => {
    downloads = captureDownloads();
    openSpy = vi.fn();
    lastRun = signal({
      setId: 'set-1',
      channelName: 'somechannel',
      result: {
        doneIds: ['e1'],
        doneKeys: ['e1'],
        items: [
          { key: 'e1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
        ],
        startedAt: Date.parse('2026-09-01T12:00:00Z'),
        finishedAt: Date.parse('2026-09-01T12:05:00Z'),
      },
    });

    await TestBed.configureTestingModule({
      imports: [
        MassDeletePanel,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: EmoteAdminService, useValue: {} as unknown as EmoteAdminService },
        {
          provide: SevenTvDeleteService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            lastRun,
          } as unknown as SevenTvDeleteService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
          } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvRunArbiter,
          useValue: {
            activeRun: signal<SevenTvRunKind | null>(null),
          } as unknown as SevenTvRunArbiter,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: Dialog, useValue: { open: openSpy } as unknown as Dialog },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');

    fixture = TestBed.createComponent(MassDeletePanel);
    panel = fixture.componentInstance;
    fixture.componentRef.setInput('setId', 'set-1');
    fixture.componentRef.setInput('channelName', 'somechannel');
    fixture.componentRef.setInput('selectedEmotes', []);
    fixture.detectChanges();
  });

  afterEach(() => {
    // Spies only (URL.createObjectURL/revokeObjectURL, document.createElement) — matching
    // file-download.spec.ts's own cleanup, never replacing the global URL object outright.
    vi.restoreAllMocks();
  });

  it('downloads the CSV protocol and marks it saved when the csv option is chosen', () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'csv', scope: 'visible' }) });

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toBe('emotepurge_somechannel_purge_2026-09-01-1205.csv');
    expect(downloads[0].mimeType).toBe(CSV_MIME);
    expect(panel['protocolSaved']()).toBe(true);
  });

  it('downloads the JSON protocol and marks it saved when the json option is chosen', () => {
    openSpy.mockReturnValue({ closed: of({ optionId: 'json', scope: 'visible' }) });

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(1);
    expect(downloads[0].filename).toBe('emotepurge_somechannel_purge_2026-09-01-1205.json');
    expect(downloads[0].mimeType).toBe(JSON_MIME);
    expect(panel['protocolSaved']()).toBe(true);
  });

  it('downloads nothing and leaves protocolSaved alone when the dialog closes with nothing', () => {
    openSpy.mockReturnValue({ closed: of(undefined) });

    panel['openProtocolExport']();

    expect(downloads).toHaveLength(0);
    expect(panel['protocolSaved']()).toBe(false);
  });
});
