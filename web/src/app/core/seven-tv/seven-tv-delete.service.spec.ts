import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { SevenTvTokenService } from './seven-tv-token.service';

// Only the keys this service actually translates — not the full app translation file.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig oder abgelaufen — bitte neues 7TV-Token eintragen.',
      rateLimited: 'Zu viele Anfragen an 7TV (Rate Limit) — später erneut versuchen.',
      networkError: 'Keine Verbindung zu 7TV möglich (Netzwerkfehler).',
      genericStatus: '7TV-Fehler (Status {{ status }}).',
    },
  },
};

const GQL_ENDPOINT = 'https://7tv.io/v3/gql';
const SYNC_ENDPOINT = '/api/channels/sensitron/emotes/sync-deleted';
const DELETE_DELAY_MS = 275;

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

describe('SevenTvDeleteService', () => {
  let service: SevenTvDeleteService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    // Translations load asynchronously even with the synchronous TestingLoader — without this,
    // a translate() call in the same tick as a test's assertions would still see no data loaded
    // yet and fall back to returning the raw key.
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(SevenTvDeleteService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  // Drives one emote all the way through the 7TV queue and returns the closing sync-deleted request,
  // which is what the sync-report tests below are actually about.
  function runOneDeleteToSyncRequest() {
    service.startDelete('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    return httpMock.expectOne(SYNC_ENDPOINT);
  }

  describe('sync report', () => {
    it('reports success only when the backend archived everything', () => {
      runOneDeleteToSyncRequest().flush({ archivedCount: 1, notFoundIds: [] });

      expect(service.syncReport()).toBe('succeeded');
    });

    it('treats an under-count as partial — all ids in notFoundIds used to look like success', () => {
      runOneDeleteToSyncRequest().flush({ archivedCount: 0, notFoundIds: ['internal-1'] });

      expect(service.syncReport()).toBe('partial');
    });

    it('retries a transient failure and succeeds on the second attempt', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 429, statusText: 'Too Many Requests' });
      expect(service.syncReport()).toBe('pending');

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 1, notFoundIds: [] });

      expect(service.syncReport()).toBe('succeeded');
    });

    it('gives up after the automatic retries are exhausted', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 500, statusText: 'Server Error' });

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 500, statusText: 'Server Error' });

      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 500, statusText: 'Server Error' });

      expect(service.syncReport()).toBe('failed');
    });

    it('does not retry a 401 — an expired session cannot be fixed by waiting', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(service.syncReport()).toBe('failed');
      vi.advanceTimersByTime(10_000);
      httpMock.verify(); // no further attempt was made
    });

    it('retrySyncReport() re-sends the same ids after a failure', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

      service.retrySyncReport();

      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(retryReq.request.body).toEqual({ emoteIds: ['internal-1'] });
      retryReq.flush({ archivedCount: 1, notFoundIds: [] });

      expect(service.syncReport()).toBe('succeeded');
    });

    it('reset() clears the sync report as well', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

      service.reset();

      expect(service.syncReport()).toBe('idle');
    });
  });

  it('does nothing without a stored token', () => {
    tokenService.clearToken();

    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.isRunning()).toBe(false);
    expect(service.queue()).toEqual([]);
  });

  it('does nothing when the emote list is empty', () => {
    service.startDelete('set-1', 'sensitron', []);

    expect(service.isRunning()).toBe(false);
  });

  it('deletes emotes sequentially with a delay, then syncs the archived ids', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.isRunning()).toBe(true);
    expect(service.queue().map((i) => i.status)).toEqual(['in-progress', 'pending']);

    const req1 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req1.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req1.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    req1.flush({});

    expect(service.queue()[0].status).toBe('done');
    expect(service.queue()[1].status).toBe('pending');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    expect(service.queue()[1].status).toBe('in-progress');

    const req2 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req2.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-2' });
    req2.flush({});

    vi.advanceTimersByTime(DELETE_DELAY_MS);

    expect(service.isRunning()).toBe(false);
    expect(service.progress()).toEqual({ finished: 2, total: 2 });

    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteIds: ['internal-1', 'internal-2'] });
    syncReq.flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('marks a GraphQL error response as failed and continues the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'emote not found' }] });

    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toBe('emote not found');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    // Only the successful one gets synced back to Postgres.
    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteIds: ['internal-2'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });
  });

  it('clears the stored token and reports a friendly message on 401', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(tokenService.hasToken()).toBe(false);
    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toContain('Token ungültig');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 1, notFoundIds: [] });
  });

  it('cancel() marks pending/in-progress items as cancelled and stops the run', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});

    // Second item is now 'pending', waiting out the inter-request delay — cancel before it fires.
    service.cancel();

    expect(service.isRunning()).toBe(false);
    expect(service.queue().map((i) => i.status)).toEqual(['done', 'cancelled']);

    // Only the already-done emote gets synced back.
    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteIds: ['internal-1'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });

    // Advancing time afterwards must not fire the second (cancelled) request.
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('reset() clears the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    service.cancel();
    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 1, notFoundIds: [] });

    service.reset();

    expect(service.queue()).toEqual([]);
  });

  it('reset() also drops the retry state — retrySyncReport() afterwards sends nothing', () => {
    runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

    service.reset();
    service.retrySyncReport();

    httpMock.expectNone(SYNC_ENDPOINT);
  });

  it('resetIfChannelChanged() clears a finished run when entering another channel', () => {
    runOneDeleteToSyncRequest().flush({ archivedCount: 1, notFoundIds: [] });

    service.resetIfChannelChanged('other-channel');

    expect(service.queue()).toEqual([]);
    expect(service.syncReport()).toBe('idle');
  });

  it('resetIfChannelChanged() keeps a finished run in its own channel', () => {
    runOneDeleteToSyncRequest().flush({ archivedCount: 1, notFoundIds: [] });

    service.resetIfChannelChanged('sensitron');

    expect(service.queue().length).toBe(1);
    expect(service.syncReport()).toBe('succeeded');
  });

  it('resetIfChannelChanged() leaves a running run alone, even for another channel', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    service.resetIfChannelChanged('other-channel');

    expect(service.isRunning()).toBe(true);
    expect(service.queue().length).toBe(2);

    // Drain the run so afterEach's httpMock.verify() stays green.
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('does not start a second run while one is already in progress', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    const firstQueueLength = service.queue().length;

    service.startDelete('set-2', 'other-channel', [EMOTES[0]]);

    expect(service.queue().length).toBe(firstQueueLength);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 2, notFoundIds: [] });
  });
});
