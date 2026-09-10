import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';
import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { ResyncTriggerState } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { ImportProgressSection } from './import-progress-section';

// Only the keys this component and the `RunProgressPanel` it wraps actually translate — not the
// full app translation file.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen', close: 'Schließen' },
  import: {
    duplicateCheckUnavailable:
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    progress: '{{ finished }} / {{ total }} kopiert',
    deleteFailedFallback: 'Kopieren fehlgeschlagen',
    rateLimitPaused: '7TV-Rate-Limit erreicht.',
    syncFailedTitle: 'Rückmeldung fehlgeschlagen',
    syncFailed: 'Rückmeldung an EmotePurge fehlgeschlagen.',
    syncRetry: 'Erneut melden',
    syncRetrySucceeded: 'Rückmeldung erfolgreich.',
    summary: {
      counts: '{{done}} kopiert · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen',
      target: 'Ziel: {{ channel }}',
      openTarget: 'Zielkanal öffnen',
      insufficientPrivileges: 'Das 7TV-Token hat im Zielset kein Schreibrecht.',
    },
    resync: {
      pending: 'Abgleich des Zielkanals wird angestoßen…',
      succeeded: 'Abgleich angestoßen — der Zielkanal zeigt die Emotes gleich.',
      cooldown: 'Der Zielkanal übernimmt die Emotes beim nächsten Abgleich.',
      failed: 'Abgleich konnte nicht angestoßen werden.',
    },
  },
};

function runInfo(overrides: Partial<ImportRunInfo> = {}): ImportRunInfo {
  return {
    targetChannelName: 'zielkanal',
    targetSetId: 'set-1',
    origin: { kind: 'channel', channelName: 'quellkanal' },
    result: null,
    ...overrides,
  };
}

/** The fake stands in for the whole service — every field the template reads is a signal this
 *  spec drives directly, exactly the shape `SevenTvImportService` presents (Regel 12: behaviour,
 *  not the service's own internals, which have their own tests). */
interface FakeImportService {
  queue: WritableSignal<RunQueueItem[]>;
  isRunning: WritableSignal<boolean>;
  rateLimitPauseSeconds: WritableSignal<number | null>;
  run: WritableSignal<ImportRunInfo | null>;
  syncReport: WritableSignal<SyncReportState>;
  resyncTrigger: WritableSignal<ResyncTriggerState>;
  abortedForPrivileges: WritableSignal<boolean>;
  skippedDuplicates: WritableSignal<number>;
  duplicateCheckAvailable: WritableSignal<boolean>;
  cancel: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
  retrySyncReport: ReturnType<typeof vi.fn>;
}

function createFakeImportService(): FakeImportService {
  return {
    queue: signal<RunQueueItem[]>([]),
    isRunning: signal(false),
    rateLimitPauseSeconds: signal<number | null>(null),
    run: signal<ImportRunInfo | null>(null),
    syncReport: signal<SyncReportState>('idle'),
    resyncTrigger: signal<ResyncTriggerState>('idle'),
    abortedForPrivileges: signal(false),
    skippedDuplicates: signal(0),
    duplicateCheckAvailable: signal(true),
    cancel: vi.fn(),
    reset: vi.fn(),
    retrySyncReport: vi.fn(),
  };
}

describe('ImportProgressSection', () => {
  let importService: FakeImportService;

  beforeEach(async () => {
    importService = createFakeImportService();

    await TestBed.configureTestingModule({
      imports: [
        ImportProgressSection,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideRouter([]), { provide: SevenTvImportService, useValue: importService }],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): ComponentFixture<ImportProgressSection> {
    const fixture = TestBed.createComponent(ImportProgressSection);
    fixture.detectChanges();
    return fixture;
  }

  it('renders nothing while idle — no run in flight and nothing left to show', () => {
    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('appears while a run is in flight, even before the queue has any items yet', () => {
    importService.isRunning.set(true);
    importService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('Ziel: zielkanal');
  });

  it('stays visible after the run has settled, as long as the queue is not empty', () => {
    importService.isRunning.set(false);
    importService.queue.set([{ key: 'a', sevenTvEmoteId: '7tv-a', name: 'A', status: 'done' }]);
    importService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('Ziel: zielkanal');
  });

  it('renders nothing once the queue is cleared and no run is in flight', () => {
    importService.isRunning.set(false);
    importService.queue.set([]);
    importService.run.set(runInfo());

    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing when the outer condition holds but no run object exists yet', () => {
    // isRunning() true would normally imply a run() — this pins down that the inner `@if
    // (importService.run(); as run)` guard is load-bearing on its own, not merely redundant.
    importService.isRunning.set(true);
    importService.run.set(null);

    const fixture = render();

    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  // The resync notice lives in `RunProgressPanel`'s `[run-actions]` slot, which that panel only
  // projects once the run has settled (`!isRunning() && total() > 0`, see run-progress-panel.ts) —
  // so every case below is post-run, not mid-run, matching how the real service actually sets
  // `resyncTrigger` (only from `onRunComplete`, after `isRunning` has already gone back to false).
  it('shows no resync notice once settled while resyncTrigger is idle', () => {
    importService.isRunning.set(false);
    importService.queue.set([{ key: 'a', sevenTvEmoteId: '7tv-a', name: 'A', status: 'done' }]);
    importService.run.set(runInfo());
    importService.resyncTrigger.set('idle');

    const fixture = render();

    expect(fixture.nativeElement.querySelector('span[role="status"]')).toBeNull();
  });

  it.each([
    ['pending', 'Abgleich des Zielkanals wird angestoßen…'],
    ['succeeded', 'Abgleich angestoßen — der Zielkanal zeigt die Emotes gleich.'],
    ['cooldown', 'Der Zielkanal übernimmt die Emotes beim nächsten Abgleich.'],
    ['failed', 'Abgleich konnte nicht angestoßen werden.'],
  ] as const)(
    'maps the settled resyncTrigger %s to its own notice text',
    (trigger, expectedText) => {
      importService.isRunning.set(false);
      importService.queue.set([{ key: 'a', sevenTvEmoteId: '7tv-a', name: 'A', status: 'done' }]);
      importService.run.set(runInfo());
      importService.resyncTrigger.set(trigger);

      const fixture = render();

      expect(fixture.nativeElement.querySelector('span[role="status"]')?.textContent).toBe(
        expectedText,
      );
    },
  );

  it('shows the insufficient-privileges banner only when the run aborted for that reason', () => {
    importService.isRunning.set(false);
    importService.queue.set([{ key: 'a', sevenTvEmoteId: '7tv-a', name: 'A', status: 'failed' }]);
    importService.run.set(runInfo());
    importService.abortedForPrivileges.set(false);

    const withoutBanner = render();
    expect(withoutBanner.nativeElement.textContent).not.toContain(
      'Das 7TV-Token hat im Zielset kein Schreibrecht.',
    );

    importService.abortedForPrivileges.set(true);
    const withBanner = render();
    expect(withBanner.nativeElement.textContent).toContain(
      'Das 7TV-Token hat im Zielset kein Schreibrecht.',
    );
  });

  // #149: the fresh pre-send duplicate check's own fetch can fail — this notice is what tells the
  // user a duplicate may have slipped in undetected, independent of the run-progress panel (shown
  // even while idle, same reasoning as skippedDuplicatesKey above it).
  it('shows nothing while the duplicate check is available (the default)', () => {
    importService.duplicateCheckAvailable.set(true);

    const fixture = render();

    expect(fixture.nativeElement.textContent).not.toContain('Wir konnten gerade nicht prüfen');
  });

  it('shows the quiet notice once the duplicate check is reported unavailable, stating the consequence', () => {
    importService.duplicateCheckAvailable.set(false);

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain(
      'Wir konnten gerade nicht prüfen, ob diese Emotes schon im Zielset sind — es können doppelte Einträge entstehen.',
    );
  });
});
