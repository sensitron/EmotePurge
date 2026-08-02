import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { RUN_DELAY_MS, RunQueueEmote } from './seven-tv-run-engine';
import { SevenTvRestoreService } from './seven-tv-restore.service';
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

const EMOTES: RunQueueEmote[] = [
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
});
