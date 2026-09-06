import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DELETE_DELAY_MS, DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SevenTvRestoreService } from './seven-tv-restore.service';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { SevenTvTokenService } from './seven-tv-token.service';

const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig.',
      rateLimited: 'Rate Limit.',
      networkError: 'Netzwerkfehler.',
      genericStatus: '7TV-Fehler ({{ status }}).',
      rateLimitedGaveUp: 'Übersprungen.',
    },
  },
};

const GQL_ENDPOINT = 'https://7tv.io/v3/gql';
const RESYNC_ENDPOINT = '/api/channels/sensitron/resync';
const SYNC_RESTORED_ENDPOINT = '/api/channels/sensitron/emotes/sync-restored';

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

describe('SevenTvRestoreService', () => {
  let service: SevenTvRestoreService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(SevenTvRestoreService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('keys every queue row by its emoteId', () => {
    service.startRestore('set-1', 'sensitron', EMOTES);

    expect(service.queue().map((item) => item.key)).toEqual(['internal-1', 'internal-2']);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 2, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('sends the ADD mutation with set id, emote id and the alias to restore under', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req.request.body.query).toContain('action: ADD');
    expect(req.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      name: 'PogU',
    });
    req.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports the finished run to sync-restored with the restored internal ids', () => {
    service.startRestore('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(reportReq.request.body).toEqual({ emoteIds: ['internal-1', 'internal-2'] });
    expect(service.syncReport()).toBe('pending');
    reportReq.flush({ restoredCount: 2, notFoundIds: [] });

    expect(service.syncReport()).toBe('succeeded');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('marks the report partial when the backend restored fewer than reported', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({ restoredCount: 0, notFoundIds: ['internal-1'] });

    expect(service.syncReport()).toBe('partial');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('marks the report failed on a 401 and re-sends it via retrySyncReport()', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    // 401 is not retried automatically — waiting cannot fix an expired session.
    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.syncReport()).toBe('failed');

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(retryReq.request.body).toEqual({ emoteIds: ['internal-1'] });
    retryReq.flush({ restoredCount: 1, notFoundIds: [] });

    expect(service.syncReport()).toBe('succeeded');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('triggers exactly one resync after the run and reports success', () => {
    service.startRestore('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 2, notFoundIds: [] });
    const resyncReq = httpMock.expectOne(RESYNC_ENDPOINT);
    expect(service.resyncTrigger()).toBe('pending');
    resyncReq.flush(null, { status: 202, statusText: 'Accepted' });

    expect(service.resyncTrigger()).toBe('succeeded');
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  it('reports the resync cooldown as "coming on its own", not as a failure', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock
      .expectOne(RESYNC_ENDPOINT)
      .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

    expect(service.resyncTrigger()).toBe('cooldown');
  });

  it('skips both the report and the resync entirely when nothing was restored', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'set is full' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(service.queue()[0].status).toBe('failed');
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
    httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  it('reset() clears queue, report and resync state', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

    service.reset();

    expect(service.queue()).toEqual([]);
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
  });

  // R15 (#72, T12): finish() flips isRunning() to false *before* the two closing calls resolve, so
  // a second run can legitimately start while the first one's report/resync are still in flight.
  // Their late answers must not land on the second run's state.
  describe('superseded run (R15)', () => {
    it('discards a late sync-restored answer from a superseded run without touching the new one', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const staleSyncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      // Settle the resync half immediately — this case is about the sync-restored guard alone.
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.syncReport()).toBe('pending');

      // A second run starts, for a different channel, before run 1's sync-restored answer comes
      // back — legitimate, because finish() already flipped isRunning() to false.
      service.startRestore('set-2', 'other-channel', [EMOTES[1]]);
      expect(service.isRunning()).toBe(true);
      expect(service.syncReport()).toBe('idle'); // run 2's own state, reset at start

      staleSyncReq.flush({ restoredCount: 1, notFoundIds: [] });
      expect(service.syncReport()).toBe('idle'); // still run 2's state, untouched by run 1's answer

      // Run 2 finishes normally afterwards — the guard must not have swallowed its own terminal
      // flank along with the stale one.
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne('/api/channels/other-channel/emotes/sync-restored')
        .flush({ restoredCount: 1, notFoundIds: [] });
      expect(service.syncReport()).toBe('succeeded');
      httpMock
        .expectOne('/api/channels/other-channel/resync')
        .flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('never lets a stale resync answer overwrite a later state — including "cooldown"', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      const staleResyncReq = httpMock.expectOne(RESYNC_ENDPOINT);

      // A second run starts, runs to completion, and its own resync lands in cooldown — a real
      // state, not the guard's doing.
      service.startRestore('set-2', 'other-channel', [EMOTES[1]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne('/api/channels/other-channel/emotes/sync-restored')
        .flush({ restoredCount: 1, notFoundIds: [] });
      httpMock
        .expectOne('/api/channels/other-channel/resync')
        .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });
      expect(service.resyncTrigger()).toBe('cooldown');

      // Run 1's late resync answer must not disturb run 2's already-settled 'cooldown'.
      staleResyncReq.flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('cooldown');
    });
  });

  // The arbiter (#70, Task 4) has no lock of its own — it reads this service's own isRunning
  // signal, so these cases pin the invariants a hand-kept tryAcquire/release could not have
  // guaranteed (see R1 in docs/DECISIONS.md): the derived state can never outlive the run it
  // describes, not even across cancel(), a start the engine itself refused, or a hand-off to the
  // sibling delete service once this run has ended.
  describe('run arbiter', () => {
    let arbiter: SevenTvRunArbiter;

    beforeEach(() => {
      arbiter = TestBed.inject(SevenTvRunArbiter);
    });

    it('reports "restore" as the active run while this service runs, then null once it ends', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

      expect(arbiter.activeRun()).toBe('restore');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once cancel() ends it', () => {
      service.startRestore('set-1', 'sensitron', EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.cancel();

      expect(arbiter.activeRun()).toBeNull();

      // Drain the closing sync-restored/resync calls so afterEach's httpMock.verify() stays green.
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('leaves no active run when the engine refuses the start for a cleared token', () => {
      tokenService.clearToken();

      service.startRestore('set-1', 'sensitron', EMOTES);

      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBeNull();
    });

    it('lets a delete start once this restore has ended — the cross-service invariant a held lock could not guarantee', () => {
      const deleteService = TestBed.inject(SevenTvDeleteService);

      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      expect(arbiter.activeRun()).toBe('restore');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

      expect(arbiter.activeRun()).toBeNull();

      deleteService.startDelete('set-1', 'sensitron', [EMOTES[1]]);

      expect(arbiter.activeRun()).toBe('delete');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock
        .expectOne('/api/channels/sensitron/emotes/sync-deleted')
        .flush({ archivedCount: 1, notFoundIds: [] });
    });
  });
});
